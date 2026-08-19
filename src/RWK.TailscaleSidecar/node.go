/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
package main

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"strings"
	"sync"
	"time"

	"tailscale.com/client/local"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tailcfg"
	"tailscale.com/tsnet"
)

// Config holds the process-level settings supplied by the supervisor.
type Config struct {
	Hostname        string
	StateDir        string
	Ephemeral       bool
	ControlURL      string
	EdgeTailnetPort int
	PollInterval    time.Duration
	FaultAfter      int
	StartTimeout    time.Duration
	Watchdog        time.Duration
	Verbose         bool
}

type socksInfo struct {
	Address  string `json:"address"`
	Username string `json:"username"`
	Password string `json:"password"`
}

type localAPIInfo struct {
	Address  string `json:"address"`
	Password string `json:"password"`
}

// probeSample is the most recent peer probe result, the source of RTT (5.4) and
// the corroborating source of path type (5.3) and DERP region (5.5).
type probeSample struct {
	At       time.Time
	OK       bool
	RTT      time.Duration
	Endpoint string
	DerpID   int
	DerpCode string
	Err      string
}

// StatusDocument is the polled status contract consumed by the C# wrapper.
// Field names are part of the IPC contract.
type StatusDocument struct {
	Protocol     int       `json:"protocol"`
	State        NodeState `json:"state"`
	BackendState string    `json:"backendState"`
	// Userspace is always true and exists so the C# side can assert
	// requirement 5.1 rather than trust it.
	Userspace bool   `json:"userspace"`
	Hostname  string `json:"hostname"`

	SelfAddress string `json:"selfAddress"`
	SelfDnsName string `json:"selfDnsName"`

	PeerSpec    string `json:"peerSpec"`
	PeerAddress string `json:"peerAddress"`
	PeerDnsName string `json:"peerDnsName"`
	PeerOnline  bool   `json:"peerOnline"`

	Path PathType `json:"path"`
	// RoundTripMs is -1 when no fresh measurement exists.
	RoundTripMs float64 `json:"roundTripMs"`
	// DerpRegion is empty unless Path is Derp.
	DerpRegion    string `json:"derpRegion"`
	ProbeAgeMs    int64  `json:"probeAgeMs"`
	ProbeFailures int    `json:"probeFailures"`
	ProbeError    string `json:"probeError,omitempty"`

	Edge     edgeStatus    `json:"edge"`
	Forwards []forwardInfo `json:"forwards"`

	Socks5   *socksInfo    `json:"socks5,omitempty"`
	LocalAPI *localAPIInfo `json:"localApi,omitempty"`

	AuthURL   string `json:"authUrl,omitempty"`
	LastError string `json:"lastError,omitempty"`
}

// Node owns the embedded userspace Tailscale node and everything derived from it.
type Node struct {
	cfg  Config
	logf func(string, ...any)
	edge *edgeRelay
	fwd  *forwardManager

	mu       sync.Mutex
	srv      *tsnet.Server
	lc       *local.Client
	started  bool
	starting bool
	stopping bool
	closed   bool

	everRunning bool
	backend     string
	lastErr     string
	authURL     string

	selfV4  netip.Addr
	selfDNS string

	peerSpec     string
	peerEdgePort int
	peerAddr     netip.Addr
	peerDNS      string
	peerOnline   bool
	curAddr      string
	relay        string

	probe          probeSample
	probeFailures  int
	statusFailures int

	socks    *socksInfo
	localAPI *localAPIInfo

	pollCancel context.CancelFunc
	pollDone   chan struct{}
}

func NewNode(cfg Config, logf func(string, ...any)) *Node {
	n := &Node{cfg: cfg, logf: logf}
	n.edge = newEdgeRelay(logf)
	n.fwd = newForwardManager(n, logf)
	return n
}

// AttachLocalEdge installs the loopback UDP socket shared with the .NET app.
func (n *Node) AttachLocalEdge(pc net.PacketConn) { n.edge.AttachLocal(pc) }

// SetEdgeCallback sets the loopback endpoint inbound edges are delivered to.
func (n *Node) SetEdgeCallback(addr string) error {
	ua, err := net.ResolveUDPAddr("udp4", addr)
	if err != nil {
		return err
	}
	if !ua.IP.IsLoopback() {
		return fmt.Errorf("edge callback address %s is not loopback; the sidecar only delivers edges to the local process", ua)
	}
	n.edge.SetCallback(ua)
	return nil
}

var errAlreadyStarted = errors.New("node already started")

// Start joins the tailnet with a pre-auth key (requirement 5.2).
//
// It returns as soon as the attempt is under way; progress and failure are
// reported through the polled status document, matching the 2s polling model.
func (n *Node) Start(authKey string) error {
	n.mu.Lock()
	if n.closed {
		n.mu.Unlock()
		return errors.New("node closed")
	}
	if n.started || n.starting {
		n.mu.Unlock()
		return errAlreadyStarted
	}
	n.starting = true
	n.stopping = false
	n.lastErr = ""
	n.authURL = ""
	n.backend = "Starting"
	n.mu.Unlock()

	go n.bringUp(authKey)
	return nil
}

func (n *Node) bringUp(authKey string) {
	// Tun is deliberately left nil: tsnet then uses its gVisor userspace
	// TCP/IP stack, so there is no TUN adapter and no privilege requirement
	// (requirement 5.1). Do not set Tun here.
	srv := &tsnet.Server{
		Dir:        n.cfg.StateDir,
		Hostname:   n.cfg.Hostname,
		AuthKey:    authKey,
		Ephemeral:  n.cfg.Ephemeral,
		ControlURL: n.cfg.ControlURL,
		UserLogf:   n.userLogf,
	}
	if n.cfg.Verbose {
		srv.Logf = func(format string, args ...any) { n.logf("ts: "+format, args...) }
	} else {
		srv.Logf = func(string, ...any) {}
	}

	n.mu.Lock()
	n.srv = srv
	n.mu.Unlock()

	record := func(stage string, err error) {
		n.logf("start failed at %s: %v", stage, err)
		n.mu.Lock()
		n.srv = nil
		n.starting = false
		n.started = false
		n.lastErr = fmt.Sprintf("%s: %v", stage, err)
		n.backend = ""
		n.mu.Unlock()
	}
	// fail is for stages after Start succeeded, where the server owns resources
	// that must be released. tsnet documents that Close must not be called
	// before Start, so a Start failure uses record alone.
	fail := func(stage string, err error) {
		_ = srv.Close()
		record(stage, err)
	}

	if err := srv.Start(); err != nil {
		record("tsnet start", err)
		return
	}

	ctx, cancel := context.WithTimeout(context.Background(), n.cfg.StartTimeout)
	defer cancel()

	st, err := srv.Up(ctx)
	if err != nil {
		fail("tailnet up", err)
		return
	}

	lc, err := srv.LocalClient()
	if err != nil {
		fail("local client", err)
		return
	}

	ip4, _ := srv.TailscaleIPs()
	if !ip4.IsValid() {
		fail("tailscale address", errors.New("no IPv4 address assigned"))
		return
	}

	// Loopback SOCKS5 proxy plus LocalAPI, both credential protected by tsnet.
	// Exposed for the C# side; the ports are chosen by tsnet so nothing is
	// hardcoded.
	var socks *socksInfo
	var lapi *localAPIInfo
	if addr, proxyCred, apiCred, lerr := srv.Loopback(); lerr != nil {
		n.logf("loopback proxy unavailable: %v (SOCKS5 disabled; TCP forwards still work)", lerr)
	} else {
		socks = &socksInfo{Address: addr, Username: "tsnet", Password: proxyCred}
		lapi = &localAPIInfo{Address: addr, Password: apiCred}
	}

	// True UDP over the mesh: gVisor gives a real PacketConn, so edge datagram
	// boundaries survive end to end (requirement 5.6).
	listenAddr := netip.AddrPortFrom(ip4, uint16(n.cfg.EdgeTailnetPort)).String()
	pc, err := srv.ListenPacket("udp4", listenAddr)
	if err != nil {
		fail("edge udp listen", err)
		return
	}
	edgePort := portOf(pc.LocalAddr())

	selfDNS := ""
	if st != nil && st.Self != nil {
		selfDNS = strings.TrimSuffix(st.Self.DNSName, ".")
	}

	pollCtx, pollCancel := context.WithCancel(context.Background())
	done := make(chan struct{})

	n.mu.Lock()
	n.lc = lc
	n.started = true
	n.starting = false
	n.everRunning = true
	n.backend = "Running"
	n.selfV4 = ip4
	n.selfDNS = selfDNS
	n.socks = socks
	n.localAPI = lapi
	n.pollCancel = pollCancel
	n.pollDone = done
	n.mu.Unlock()

	n.edge.AttachTailnet(pc, edgePort)
	n.applyPeerToEdge()

	n.logf("joined tailnet as %s (%s); edge udp on %s:%d", selfDNS, ip4, ip4, edgePort)

	go func() {
		defer close(done)
		n.poll(pollCtx)
	}()
}

func (n *Node) userLogf(format string, args ...any) {
	msg := fmt.Sprintf(format, args...)
	n.logf("ts: %s", msg)
	// Capture the interactive login URL so the UI can show it if the auth key
	// was rejected or absent.
	if i := strings.Index(msg, "https://"); i >= 0 && strings.Contains(msg, "login") {
		n.mu.Lock()
		n.authURL = strings.Fields(msg[i:])[0]
		n.mu.Unlock()
	}
}

// Stop leaves the tailnet and releases networking resources.
func (n *Node) Stop() {
	n.mu.Lock()
	if !n.started && !n.starting {
		n.mu.Unlock()
		return
	}
	n.stopping = true
	srv := n.srv
	cancel := n.pollCancel
	done := n.pollDone
	n.srv = nil
	n.lc = nil
	n.pollCancel = nil
	n.pollDone = nil
	n.mu.Unlock()

	if cancel != nil {
		cancel()
	}
	if done != nil {
		select {
		case <-done:
		case <-time.After(3 * time.Second):
			n.logf("status poller did not stop within 3s; continuing teardown")
		}
	}

	// Tailnet-backed resources must go before the server they belong to.
	n.fwd.CloseTailnetForwards()
	n.edge.DetachTailnet()

	if srv != nil {
		if err := srv.Close(); err != nil {
			n.logf("tsnet close: %v", err)
		}
	}

	n.mu.Lock()
	n.started = false
	n.starting = false
	n.stopping = false
	n.backend = ""
	n.socks = nil
	n.localAPI = nil
	n.probe = probeSample{}
	n.probeFailures = 0
	n.statusFailures = 0
	n.curAddr = ""
	n.relay = ""
	n.mu.Unlock()

	n.logf("left tailnet")
}

// Close performs the final teardown. Idempotent.
func (n *Node) Close() {
	n.mu.Lock()
	if n.closed {
		n.mu.Unlock()
		return
	}
	n.closed = true
	n.mu.Unlock()

	n.Stop()
	n.fwd.Close()
	n.edge.Close()
}

// SetPeer records the peer this node exchanges edges and control traffic with.
// edgePort is the peer's tailnet UDP port for edges, which the applications
// exchange over the control channel; 0 leaves outbound edges undeliverable and
// is reported as dropNoPeer in the status document.
func (n *Node) SetPeer(spec string, edgePort int) error {
	spec = strings.TrimSpace(spec)
	if spec == "" {
		return errors.New("peer address or hostname is required")
	}
	if edgePort < 0 || edgePort > 65535 {
		return fmt.Errorf("edgePort %d out of range", edgePort)
	}

	n.mu.Lock()
	n.peerSpec = spec
	n.peerEdgePort = edgePort
	n.peerAddr = netip.Addr{}
	n.peerDNS = ""
	n.peerOnline = false
	n.probe = probeSample{}
	n.probeFailures = 0
	lc := n.lc
	n.mu.Unlock()

	// If the spec is already a literal address the edge relay can be pointed at
	// it immediately; otherwise the next poll resolves it from the netmap.
	if addr, err := netip.ParseAddr(spec); err == nil {
		n.mu.Lock()
		n.peerAddr = addr
		n.mu.Unlock()
	}
	n.applyPeerToEdge()

	if lc != nil {
		go n.refresh(context.Background())
	}
	return nil
}

func (n *Node) applyPeerToEdge() {
	n.mu.Lock()
	addr, port := n.peerAddr, n.peerEdgePort
	n.mu.Unlock()

	if addr.IsValid() && port > 0 {
		n.edge.SetPeer(netip.AddrPortFrom(addr, uint16(port)))
	}
}

func (n *Node) poll(ctx context.Context) {
	interval := n.cfg.PollInterval
	if interval <= 0 {
		interval = 2 * time.Second
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	n.refresh(ctx)
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			n.refresh(ctx)
		}
	}
}

// refresh updates the netmap-derived fields and, when a peer is known, probes it
// for RTT and path type.
func (n *Node) refresh(ctx context.Context) {
	n.mu.Lock()
	lc := n.lc
	spec := n.peerSpec
	n.mu.Unlock()
	if lc == nil {
		return
	}

	sctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	st, err := lc.Status(sctx)
	cancel()
	if err != nil {
		// A single hiccup querying the local backend must not flip the state to
		// Fault, because Fault drives the Station's F9 key-up. Require the same
		// threshold used for peer probes.
		n.mu.Lock()
		n.statusFailures++
		failures := n.statusFailures
		if n.cfg.FaultAfter <= 0 || failures >= n.cfg.FaultAfter {
			n.backend = ""
		}
		n.mu.Unlock()
		n.logf("status query failed (%d consecutive): %v", failures, err)
		return
	}

	peerAddr, peerDNS, peerOnline, curAddr, relay := resolvePeer(st, spec)

	n.mu.Lock()
	n.statusFailures = 0
	n.backend = st.BackendState
	if st.BackendState == "Running" {
		n.everRunning = true
	}
	if st.Self != nil {
		n.selfDNS = strings.TrimSuffix(st.Self.DNSName, ".")
	}
	if peerAddr.IsValid() {
		n.peerAddr = peerAddr
	}
	n.peerDNS = peerDNS
	n.peerOnline = peerOnline
	n.curAddr = curAddr
	n.relay = relay
	probeTarget := n.peerAddr
	n.mu.Unlock()

	n.applyPeerToEdge()

	if !probeTarget.IsValid() {
		return
	}

	pctx, pcancel := context.WithTimeout(ctx, 4*time.Second)
	res, perr := lc.Ping(pctx, probeTarget, tailcfg.PingDisco)
	pcancel()

	n.mu.Lock()
	defer n.mu.Unlock()
	switch {
	case perr != nil:
		n.probeFailures++
		n.probe.OK = false
		n.probe.At = time.Now()
		n.probe.Err = perr.Error()
	case res == nil || res.Err != "":
		n.probeFailures++
		n.probe.OK = false
		n.probe.At = time.Now()
		if res != nil {
			n.probe.Err = res.Err
		} else {
			n.probe.Err = "empty ping result"
		}
	default:
		n.probeFailures = 0
		n.probe = probeSample{
			At:       time.Now(),
			OK:       true,
			RTT:      time.Duration(res.LatencySeconds * float64(time.Second)),
			Endpoint: res.Endpoint,
			DerpID:   res.DERPRegionID,
			DerpCode: res.DERPRegionCode,
		}
	}
}

// resolvePeer finds the peer in the netmap by literal address, FQDN, or
// hostname, and returns its address, DNS name, online flag and current path
// fields.
func resolvePeer(st *ipnstate.Status, spec string) (addr netip.Addr, dnsName string, online bool, curAddr, relay string) {
	if st == nil || spec == "" {
		return
	}
	// netip.ParseAddr returns (Addr, error), not a comma-ok bool, so the boolean
	// has to be derived from the error. Matches the same pattern in SetPeer.
	wantAddr, parseErr := netip.ParseAddr(spec)
	isAddr := parseErr == nil
	want := strings.ToLower(strings.TrimSuffix(spec, "."))

	for _, ps := range st.Peer {
		if ps == nil {
			continue
		}
		match := false
		if isAddr {
			for _, ip := range ps.TailscaleIPs {
				if ip == wantAddr {
					match = true
					break
				}
			}
		} else {
			psDNS := strings.ToLower(strings.TrimSuffix(ps.DNSName, "."))
			if psDNS == want || strings.EqualFold(ps.HostName, spec) {
				match = true
			} else if i := strings.Index(psDNS, "."); i > 0 && psDNS[:i] == want {
				// Allow the bare MagicDNS label.
				match = true
			}
		}
		if !match {
			continue
		}
		dnsName = strings.TrimSuffix(ps.DNSName, ".")
		online = ps.Online
		curAddr = ps.CurAddr
		relay = ps.Relay
		if isAddr {
			addr = wantAddr
		} else {
			for _, ip := range ps.TailscaleIPs {
				if ip.Is4() {
					addr = ip
					break
				}
			}
		}
		return
	}

	if isAddr {
		// Peer not in the netmap yet; the literal address is still usable.
		addr = wantAddr
	}
	return
}

// Status builds the polled status document.
func (n *Node) Status() StatusDocument {
	n.mu.Lock()
	in := stateInputs{
		Started:                  n.started,
		Stopping:                 n.stopping,
		BackendState:             n.backend,
		LastError:                n.lastErr,
		EverRunning:              n.everRunning,
		PeerConfigured:           n.peerSpec != "",
		ConsecutiveProbeFailures: n.probeFailures,
		FaultAfter:               n.cfg.FaultAfter,
	}
	doc := StatusDocument{
		Protocol:      ipcProtocolVersion,
		BackendState:  n.backend,
		Userspace:     true,
		Hostname:      n.cfg.Hostname,
		SelfDnsName:   n.selfDNS,
		PeerSpec:      n.peerSpec,
		PeerDnsName:   n.peerDNS,
		PeerOnline:    n.peerOnline,
		RoundTripMs:   -1,
		ProbeFailures: n.probeFailures,
		ProbeError:    n.probe.Err,
		Socks5:        n.socks,
		LocalAPI:      n.localAPI,
		AuthURL:       n.authURL,
		LastError:     n.lastErr,
	}
	if n.selfV4.IsValid() {
		doc.SelfAddress = n.selfV4.String()
	}
	if n.peerAddr.IsValid() {
		doc.PeerAddress = n.peerAddr.String()
	}

	path, derp := pathFromPeerStatus(n.curAddr, n.relay)
	if !n.probe.At.IsZero() {
		doc.ProbeAgeMs = time.Since(n.probe.At).Milliseconds()
	} else {
		doc.ProbeAgeMs = -1
	}
	if n.probe.OK && time.Since(n.probe.At) < 3*n.cfg.PollInterval {
		if p, d := pathFromPing(n.probe.Endpoint, n.probe.DerpID, n.probe.DerpCode); p != PathNone {
			path, derp = p, d
		}
		doc.RoundTripMs = float64(n.probe.RTT.Microseconds()) / 1000.0
	}
	n.mu.Unlock()

	doc.State = deriveState(in)
	doc.Path = path
	doc.DerpRegion = derp
	doc.Edge = n.edge.Snapshot()
	doc.Forwards = n.fwd.List()
	return doc
}

// --- tailnetTransport ---

func (n *Node) DialTailnet(ctx context.Context, network, address string) (net.Conn, error) {
	n.mu.Lock()
	srv := n.srv
	started := n.started
	n.mu.Unlock()
	if srv == nil || !started {
		return nil, errors.New("tailnet not started")
	}
	return srv.Dial(ctx, network, address)
}

func (n *Node) ListenTailnet(network, address string) (net.Listener, error) {
	n.mu.Lock()
	srv := n.srv
	started := n.started
	n.mu.Unlock()
	if srv == nil || !started {
		return nil, errors.New("tailnet not started")
	}
	return srv.Listen(network, address)
}

func (n *Node) ListenPacketTailnet(network, address string) (net.PacketConn, error) {
	n.mu.Lock()
	srv := n.srv
	started := n.started
	n.mu.Unlock()
	if srv == nil || !started {
		return nil, errors.New("tailnet not started")
	}
	return srv.ListenPacket(network, address)
}

func (n *Node) PeerHost() string {
	n.mu.Lock()
	defer n.mu.Unlock()
	if n.peerAddr.IsValid() {
		return n.peerAddr.String()
	}
	return n.peerSpec
}

func portOf(a net.Addr) int {
	if ua, ok := a.(*net.UDPAddr); ok {
		return ua.Port
	}
	_, p, err := net.SplitHostPort(a.String())
	if err != nil {
		return 0
	}
	v := 0
	for i := 0; i < len(p); i++ {
		if p[i] < '0' || p[i] > '9' {
			return 0
		}
		v = v*10 + int(p[i]-'0')
	}
	return v
}
