/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
// Command rwk-tailscale-sidecar embeds a userspace Tailscale node (tsnet) and
// exposes it to the RWK .NET applications over loopback IPC.
//
// It is supervised as a child process by RWK.Client / RWK.Station and backs the
// ITailscaleNode interface (design Component 5, requirements 5.1-5.5).
//
// Requirement 5.1 is a hard constraint: tsnet runs a gVisor userspace TCP/IP
// stack, so there is no TUN adapter and no administrator privilege anywhere in
// this program. Nothing here may be changed to a TUN-based path.
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"
)

// handshake is the single JSON line written to stdout once the IPC listener is
// up. The supervisor reads exactly one line, then may stop reading stdout.
// It carries the actual bound ports so no port has to be hardcoded on either
// side: pass -api-addr 127.0.0.1:0 and read the result from here.
type handshake struct {
	Protocol         int    `json:"protocol"`
	Pid              int    `json:"pid"`
	APIAddress       string `json:"apiAddress"`
	Token            string `json:"token"`
	EdgeLocalAddress string `json:"edgeLocalAddress"`
	EdgeTransport    string `json:"edgeTransport"`
}

// ipcProtocolVersion is bumped when the IPC contract changes incompatibly.
const ipcProtocolVersion = 1

func main() {
	cfg := Config{}

	apiAddr := flag.String("api-addr", "127.0.0.1:0",
		"loopback address for the IPC/status HTTP API; port 0 selects a free port")
	token := flag.String("token", "",
		"shared secret required in the X-RWK-Token header; generated and reported in the stdout handshake when empty")
	edgeLocalAddr := flag.String("edge-local-addr", "127.0.0.1:0",
		"loopback UDP address this process binds to receive outbound edge datagrams from the .NET app; port 0 selects a free port")
	edgeCallbackAddr := flag.String("edge-callback-addr", "",
		"loopback UDP address of the .NET app's edge receive socket; may also be set later via POST /v1/edge/callback")
	exitOnStdinClose := flag.Bool("exit-on-stdin-close", true,
		"exit when stdin reaches EOF, which happens when the supervising parent process dies")

	flag.StringVar(&cfg.Hostname, "hostname", "rwk-node",
		"hostname to present to the tailnet control plane")
	flag.StringVar(&cfg.StateDir, "state-dir", "",
		"directory holding this node's Tailscale identity and state; defaults to a per-hostname directory under the user config dir")
	flag.BoolVar(&cfg.Ephemeral, "ephemeral", false,
		"register as an ephemeral node so the tailnet identity is removed when this process exits (requires an ephemeral auth key)")
	flag.StringVar(&cfg.ControlURL, "control-url", "",
		"coordination server URL; empty uses the Tailscale default")
	flag.IntVar(&cfg.EdgeTailnetPort, "edge-tailnet-port", 0,
		"UDP port to bind on the tailnet for edge datagrams; 0 selects a free port, reported in the status document")
	flag.DurationVar(&cfg.PollInterval, "poll-interval", 2*time.Second,
		"interval for refreshing peer status and measuring RTT (design calls for 2s)")
	flag.IntVar(&cfg.FaultAfter, "fault-after", 3,
		"consecutive failed peer probes before the node reports state Fault (requirement 5.8)")
	flag.DurationVar(&cfg.StartTimeout, "start-timeout", 90*time.Second,
		"how long to wait for the tailnet to come up after /v1/start")
	flag.DurationVar(&cfg.Watchdog, "watchdog", 15*time.Second,
		"exit if no IPC request arrives within this period; 0 disables. Guards against a stranded sidecar holding a tailnet identity")
	flag.BoolVar(&cfg.Verbose, "verbose", false,
		"log verbose Tailscale backend diagnostics to stderr")

	flag.Parse()

	// All logging goes to stderr. Stdout carries the handshake line only.
	logger := log.New(os.Stderr, "[sidecar] ", log.LstdFlags|log.Lmicroseconds)
	logf := func(format string, args ...any) { logger.Printf(format, args...) }

	// Both IPC sockets are refused on anything but loopback. The control API can
	// join or leave the tailnet and open tunnels into the Station's network, so
	// binding it to a routable interface would expose that to the whole LAN.
	for flagName, addr := range map[string]string{"-api-addr": *apiAddr, "-edge-local-addr": *edgeLocalAddr} {
		if err := requireLoopback(addr); err != nil {
			fmt.Fprintf(os.Stderr, "%s %q rejected: %v\n", flagName, addr, err)
			os.Exit(2)
		}
	}

	if cfg.EdgeTailnetPort < 0 || cfg.EdgeTailnetPort > 65535 {
		fmt.Fprintf(os.Stderr, "-edge-tailnet-port must be in 0..65535, got %d\n", cfg.EdgeTailnetPort)
		os.Exit(2)
	}
	if cfg.PollInterval <= 0 {
		fmt.Fprintf(os.Stderr, "-poll-interval must be positive, got %v\n", cfg.PollInterval)
		os.Exit(2)
	}
	if cfg.Watchdog > 0 && cfg.Watchdog <= cfg.PollInterval {
		fmt.Fprintf(os.Stderr, "-watchdog (%v) must exceed -poll-interval (%v) or the sidecar will exit while healthy\n",
			cfg.Watchdog, cfg.PollInterval)
		os.Exit(2)
	}

	if cfg.StateDir == "" {
		base, err := os.UserConfigDir()
		if err != nil {
			logf("fatal: cannot determine user config dir and -state-dir was not set: %v", err)
			os.Exit(2)
		}
		cfg.StateDir = filepath.Join(base, "RWK", "tailscale", cfg.Hostname)
	}
	if err := os.MkdirAll(cfg.StateDir, 0o700); err != nil {
		logf("fatal: cannot create state dir %q: %v", cfg.StateDir, err)
		os.Exit(2)
	}

	authToken := *token
	if authToken == "" {
		var b [32]byte
		if _, err := rand.Read(b[:]); err != nil {
			logf("fatal: cannot generate IPC token: %v", err)
			os.Exit(2)
		}
		authToken = hex.EncodeToString(b[:])
	}

	// The loopback UDP socket is bound before the tailnet comes up so the
	// supervisor knows where to send edges immediately after launch.
	localEdge, err := net.ListenPacket("udp4", *edgeLocalAddr)
	if err != nil {
		logf("fatal: cannot bind edge loopback socket on %q: %v", *edgeLocalAddr, err)
		os.Exit(2)
	}

	node := NewNode(cfg, logf)
	node.AttachLocalEdge(localEdge)

	if *edgeCallbackAddr != "" {
		if err := node.SetEdgeCallback(*edgeCallbackAddr); err != nil {
			logf("fatal: invalid -edge-callback-addr %q: %v", *edgeCallbackAddr, err)
			os.Exit(2)
		}
	}

	api := NewAPI(node, authToken, logf)

	ln, err := net.Listen("tcp", *apiAddr)
	if err != nil {
		logf("fatal: cannot bind IPC API on %q: %v", *apiAddr, err)
		os.Exit(2)
	}

	shutdown, cancelShutdown := context.WithCancel(context.Background())
	defer cancelShutdown()

	// Reason recorded so the exit is attributable in the log.
	reason := make(chan string, 4)
	stop := func(why string) {
		select {
		case reason <- why:
		default:
		}
		cancelShutdown()
	}

	go func() {
		if err := api.Serve(ln); err != nil && !errors.Is(err, io.EOF) {
			logf("IPC API stopped: %v", err)
			stop("api-stopped")
		}
	}()

	// Watchdog: the C# side polls status every PollInterval. Silence for
	// longer than the watchdog period means the supervisor is gone or wedged,
	// and a sidecar holding a tailnet identity with nobody driving it is an
	// operational hazard, so exit.
	if cfg.Watchdog > 0 {
		go func() {
			ticker := time.NewTicker(cfg.Watchdog / 3)
			defer ticker.Stop()
			for {
				select {
				case <-shutdown.Done():
					return
				case <-ticker.C:
					if idle := api.IdleFor(); idle > cfg.Watchdog {
						logf("watchdog: no IPC request for %v (limit %v)", idle.Round(time.Millisecond), cfg.Watchdog)
						stop("watchdog")
						return
					}
				}
			}
		}()
	}

	// Parent death detection: when the supervisor holds our stdin and then
	// exits, the pipe closes and Read returns EOF.
	if *exitOnStdinClose {
		go func() {
			buf := make([]byte, 256)
			for {
				n, err := os.Stdin.Read(buf)
				if err != nil {
					logf("stdin closed (%v); shutting down", err)
					stop("stdin-eof")
					return
				}
				_ = n // stdin content is ignored; only closure matters
			}
		}()
	}

	signals := make(chan os.Signal, 2)
	signal.Notify(signals, os.Interrupt, syscall.SIGTERM)
	go func() {
		select {
		case s := <-signals:
			logf("received signal %v", s)
			stop("signal:" + s.String())
		case <-shutdown.Done():
		}
	}()

	hs := handshake{
		Protocol:         ipcProtocolVersion,
		Pid:              os.Getpid(),
		APIAddress:       ln.Addr().String(),
		Token:            authToken,
		EdgeLocalAddress: localEdge.LocalAddr().String(),
		EdgeTransport:    EdgeTransport,
	}
	line, err := json.Marshal(hs)
	if err != nil {
		logf("fatal: cannot encode handshake: %v", err)
		os.Exit(2)
	}
	fmt.Fprintf(os.Stdout, "%s\n", line)
	_ = os.Stdout.Sync()

	logf("ready: api=%s edgeLocal=%s edgeTransport=%s stateDir=%s",
		hs.APIAddress, hs.EdgeLocalAddress, EdgeTransport, cfg.StateDir)

	<-shutdown.Done()

	why := "unknown"
	select {
	case why = <-reason:
	default:
	}
	logf("shutting down (%s)", why)

	// Ordered teardown: stop accepting IPC, then leave the tailnet, then drop
	// the loopback socket. node.Close is idempotent.
	shutCtx, cancelShut := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancelShut()
	_ = api.Shutdown(shutCtx)
	node.Close()
	_ = localEdge.Close()

	logf("exited cleanly")
}

// requireLoopback rejects an IPC bind address that is not on the loopback
// interface. An empty host (":port") is rejected too, because it means the
// any-address.
func requireLoopback(addr string) error {
	host, _, err := net.SplitHostPort(addr)
	if err != nil {
		return fmt.Errorf("expected host:port: %w", err)
	}
	if host == "" {
		return errors.New("host is empty, which binds every interface; use 127.0.0.1")
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return fmt.Errorf("%q is not an IP literal; use 127.0.0.1", host)
	}
	if !ip.IsLoopback() {
		return fmt.Errorf("%s is not a loopback address; the IPC surface must stay on loopback", ip)
	}
	return nil
}
