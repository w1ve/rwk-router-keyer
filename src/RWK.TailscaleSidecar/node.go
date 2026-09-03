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
	selfV6  netip.Addr
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

	// ── LOGIN-HANG ROOT CAUSE & FIX ──────────────────────────────────────────
	// Historically the status poll loop (n.poll → n.refresh → lc.Status) was only
	// started AFTER srv.Up(ctx) returned. But srv.Up() BLOCKS for the entire
	// interactive browser login (open browser, sign in, approve device), which for
	// a first-time login routinely exceeds StartTimeout (default 90s). During that
	// whole window the sidecar was BLIND: n.backend was never refreshed, /v1/status
	// returned stale state, and on an Up() timeout the recovery code checked
	// lc.Status() exactly ONCE for "Running" — which is usually false right after
	// login (backend is still NeedsLogin/NeedsMachineAuth/Starting) — then called
	// fail(), closing the server and tearing the node down. The .NET wizard then
	// never saw Connected and hung forever.
	//
	// The fix makes bringUp non-blocking and status-driven:
	//   1. Grab LocalClient EARLY and set n.lc + start the poll loop IMMEDIATELY,
	//      so n.refresh()/lc.Status() keeps n.backend, authURL, self/peer fields
	//      live throughout the login window (this is exactly what must run DURING
	//      login, not after).
	//   2. Run srv.Up(ctx) to actually drive the tailnet up. On an Up() timeout we
	//      DO NOT tear the node down while the backend is legitimately progressing
	//      (NeedsLogin/NeedsMachineAuth/Starting/Running) — we let the poll loop
	//      promote it to Running when login completes.
	//   3. Once a valid IPv4 is available, run the post-up setup (loopback,
	//      edge UDP listener, edge attach, self DNS) exactly once — this preserves
	//      the fast/non-interactive path behavior byte-for-byte.

	lc, lcErr := srv.LocalClient()
	if lcErr != nil {
		fail("local client", lcErr)
		return
	}

	// Start the poll loop NOW, before Up() has a chance to block. n.refresh reads
	// n.lc under lock and returns early if nil, so n.lc must be set first. The poll
	// loop keeps /v1/status live (and the IPC watchdog fed) for the whole login.
	pollCtx, pollCancel := context.WithCancel(context.Background())
	done := make(chan struct{})

	n.mu.Lock()
	n.lc = lc
	n.started = true
	n.starting = false
	n.pollCancel = pollCancel
	n.pollDone = done
	// Leave n.backend as "Starting"; the poll loop's refresh() will update it from
	// the real BackendState (and set everRunning/clear authURL on "Running").
	n.mu.Unlock()

	go func() {
		defer close(done)
		n.poll(pollCtx)
	}()

	ctx, cancel := context.WithTimeout(context.Background(), n.cfg.StartTimeout)
	defer cancel()

	st, err := srv.Up(ctx)

	if err != nil {
		// tsnet.Up can report "context deadline exceeded" even when the backend
		// actually reached Running, and — more importantly for first-time logins —
		// it times out while the backend is still legitimately progressing through
		// the interactive login. Decide whether to tear down based on the CURRENT
		// backend state, not on Up()'s return value.
		backend := ""
		if cctx, ccancel := context.WithTimeout(context.Background(), 5*time.Second); true {
			if cst, cerr := lc.Status(cctx); cerr == nil && cst != nil {
				backend = cst.BackendState
				if cst.BackendState == "Running" {
					st = cst
				}
			}
			ccancel()
		}

		progressing := backend == "Running" || backend == "Starting" ||
			backend == "NeedsLogin" || backend == "NeedsMachineAuth"
		timeout := errors.Is(err, context.DeadlineExceeded)

		if backend == "Running" {
			n.logf("tsnet.Up returned %q but backend is Running — recovering", err)
		} else if timeout && progressing {
			// The user is still completing the interactive browser login. Do NOT
			// fail()/tear down — the poll loop is already live and will promote the
			// node to Running once login finishes. Wait (poll-driven) for a valid
			// IPv4 before doing the edge/loopback setup below.
			n.logf("tsnet.Up timed out while backend is %q (login in progress) — keeping node alive and waiting", backend)
		} else {
			// Genuine, non-recoverable start error (backend Stopped/NoState/empty
			// and not progressing). Tear down as before.
			fail("tailnet up", err)
			return
		}
	}

	// Wait for a valid IPv4 to be assigned. On the fast/non-interactive path this is
	// already true the moment Up() returns, so this loop exits immediately. On the
	// slow interactive path we may have gotten here on an Up() timeout while login is
	// still finishing; keep waiting (the poll loop keeps status/authURL live) until
	// the backend reaches Running and an address is assigned, bounded by a generous
	// overall cap so a truly wedged backend still gets torn down eventually.
	ip4, ip6 := srv.TailscaleIPs()
	if !ip4.IsValid() {
		const loginWaitCap = 10 * time.Minute
		waitCtx, waitCancel := context.WithTimeout(context.Background(), loginWaitCap)
		wticker := time.NewTicker(1 * time.Second)
	waitLoop:
		for !ip4.IsValid() {
			select {
			case <-waitCtx.Done():
				break waitLoop
			case <-pollCtx.Done():
				// Node was stopped/closed while waiting for login; nothing to do,
				// teardown is handled by Stop().
				wticker.Stop()
				waitCancel()
				return
			case <-wticker.C:
				ip4, ip6 = srv.TailscaleIPs()
			}
		}
		wticker.Stop()
		waitCancel()

		if !ip4.IsValid() {
			// Login never completed within the generous cap — treat as a genuine failure.
			fail("tailscale address", errors.New("no IPv4 address assigned (login not completed)"))
			return
		}
		// Refresh st so self DNS reflects the now-Running backend.
		if cctx, ccancel := context.WithTimeout(context.Background(), 5*time.Second); true {
			if cst, cerr := lc.Status(cctx); cerr == nil && cst != nil {
				st = cst
			}
			ccancel()
		}
	}

	// ── POST-UP SETUP (fast and slow paths converge here with a valid IPv4) ──
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
	pc, listenErr := srv.ListenPacket("udp4", listenAddr)
	if listenErr != nil {
		fail("edge udp listen", listenErr)
		return
	}
	edgePort := portOf(pc.LocalAddr())

	selfDNS := ""
	if st != nil && st.Self != nil {
		selfDNS = strings.TrimSuffix(st.Self.DNSName, ".")
	}

	n.mu.Lock()
	n.everRunning = true
	n.backend = "Running"
	n.selfV4 = ip4
	n.selfV6 = ip6
	if selfDNS != "" {
		n.selfDNS = selfDNS
	}
	n.socks = socks
	n.localAPI = lapi
	// The user has finished login (or never needed it); drop any interactive URL.
	n.authURL = ""
	n.mu.Unlock()

	n.edge.AttachTailnet(pc, edgePort)
	n.applyPeerToEdge()

	n.logf("joined tailnet as %s (%s); edge udp on %s:%d", selfDNS, ip4, ip4, edgePort)
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
//
// An EMPTY spec CLEARS the peer. This is deliberate and load-bearing: the Client
// configures a peer for a pairing ATTEMPT (before the HMAC handshake), and when
// that attempt fails against a stale/dead station IP the peer must be removed.
// If it were left configured, the poll loop would keep probing the dead peer,
// n.probeFailures would climb past FaultAfter, and deriveState would report
// Fault (because BackendState=="Running" && PeerConfigured && failures>=FaultAfter),
// which drops the link display and drives the Station's F9. Clearing the peer
// makes PeerConfigured=false, so a failed/abandoned pair can never fault the node.
func (n *Node) SetPeer(spec string, edgePort int) error {
	spec = strings.TrimSpace(spec)
	if spec == "" {
		// Clear the peer entirely: no spec, no address, no probe history. With
		// peerSpec empty, Status() reports PeerConfigured=false and deriveState
		// ignores probe failures, so an unpaired node stays Connected.
		n.mu.Lock()
		n.peerSpec = ""
		n.peerEdgePort = 0
		n.peerAddr = netip.Addr{}
		n.peerDNS = ""
		n.peerOnline = false
		n.probe = probeSample{}
		n.probeFailures = 0
		n.mu.Unlock()

		// Drop the edge relay peer so outbound edges are dropped as dropNoPeer
		// and no inbound source is accepted until a new peer is configured.
		n.edge.ClearPeer()
		return nil
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

// LoginPending reports whether the node is currently waiting for the user to complete an
// interactive browser login (an auth URL has been emitted and the node has never reached
// Running). The IPC watchdog uses this to avoid killing a healthy sidecar during the login
// window: srv.Up() blocks for the whole browser login, during which the .NET side may not
// be polling, but the sidecar must stay alive so the user can finish authenticating.
func (n *Node) LoginPending() bool {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.authURL != "" && !n.everRunning
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
		// Clear the interactive auth URL once we're connected — the user has
		// completed login and the C# side should dismiss the login panel.
		n.authURL = ""
	}
	// Also clear authURL on any state that indicates login is no longer needed
	// (e.g. "Starting", "Authenticated"). This closes the timing window where
	// the C# side sees a stale authUrl after the browser auth completes.
	if st.BackendState != "NeedsLogin" && st.BackendState != "NeedsMachineAuth" {
		n.authURL = ""
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

func (n *Node) TailnetAddrs() (ipv4, ipv6 string) {
	n.mu.Lock()
	defer n.mu.Unlock()
	if n.selfV4.IsValid() {
		ipv4 = n.selfV4.String()
	}
	if n.selfV6.IsValid() {
		ipv6 = n.selfV6.String()
	}
	return
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
