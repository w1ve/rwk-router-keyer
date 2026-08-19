package main

import (
	"errors"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"time"
)

// EdgeTransport declares how edge events cross the mesh.
//
// This is "udp": tsnet.Server.ListenPacket (available since tailscale v1.68.0)
// returns a real net.PacketConn served by the gVisor userspace stack, so edge
// events travel as genuine UDP datagrams over WireGuard with datagram
// boundaries preserved end to end. No TCP substitution takes place.
//
// The value is reported in the status document and in the stdout handshake so
// the Station can pick its jitter-buffer profile from an observed fact rather
// than an assumption. If a future change ever forces a TCP fallback, this must
// become "tcp" and JitterProfile must become jitterProfileDerpClassOnly, which
// forces the conservative DERP-class buffer at all times.
const EdgeTransport = "udp"

const (
	// jitterProfilePathAdaptive means the Station may select its jitter delay
	// from the observed path type (design 7.1: Direct 30-150ms, DERP
	// 100-500ms), because datagram fidelity is preserved.
	jitterProfilePathAdaptive = "PathAdaptive"
	// jitterProfileDerpClassOnly means the Station must use the conservative
	// DERP-class delay regardless of path type. Reported only if the edge path
	// is not true UDP.
	jitterProfileDerpClassOnly = "DerpClassOnly"
)

// maxDatagram bounds a single edge datagram. RWK-PADDLE frames carrying four
// edges are well under 64 bytes; this leaves ample headroom.
const maxDatagram = 2048

type edgeStats struct {
	txDatagrams    atomic.Uint64
	txBytes        atomic.Uint64
	rxDatagrams    atomic.Uint64
	rxBytes        atomic.Uint64
	dropNoPeer     atomic.Uint64
	dropNoCallback atomic.Uint64
	dropForeign    atomic.Uint64
	txErrors       atomic.Uint64
	rxErrors       atomic.Uint64
}

// edgeStatus is the JSON view of the edge relay.
type edgeStatus struct {
	Transport       string `json:"transport"`
	JitterProfile   string `json:"jitterProfile"`
	LocalAddress    string `json:"localAddress"`
	CallbackAddress string `json:"callbackAddress"`
	TailnetAddress  string `json:"tailnetAddress"`
	TailnetPort     int    `json:"tailnetPort"`
	PeerEndpoint    string `json:"peerEndpoint"`
	TxDatagrams     uint64 `json:"txDatagrams"`
	TxBytes         uint64 `json:"txBytes"`
	RxDatagrams     uint64 `json:"rxDatagrams"`
	RxBytes         uint64 `json:"rxBytes"`
	DropNoPeer      uint64 `json:"dropNoPeer"`
	DropNoCallback  uint64 `json:"dropNoCallback"`
	DropForeign     uint64 `json:"dropForeign"`
	TxErrors        uint64 `json:"txErrors"`
	RxErrors        uint64 `json:"rxErrors"`
}

// edgeRelay bridges a loopback UDP socket shared with the .NET application and
// a UDP socket on the tailnet (requirement 5.6).
//
// Outbound: .NET -> loopback -> tailnet -> peer.
// Inbound:  peer -> tailnet -> loopback -> .NET.
//
// Both sides are plain net.PacketConn values, so the relay can be exercised in
// tests with a pair of ordinary loopback sockets and no tailnet.
type edgeRelay struct {
	logf func(string, ...any)

	mu             sync.Mutex
	local          net.PacketConn
	tailnet        net.PacketConn
	callback       *net.UDPAddr
	callbackLocked bool // true once explicitly configured; blocks source learning
	peer           netip.AddrPort
	tailnetPort    int
	closed         bool

	wg    sync.WaitGroup
	stats edgeStats
}

func newEdgeRelay(logf func(string, ...any)) *edgeRelay {
	return &edgeRelay{logf: logf}
}

// AttachLocal installs the loopback socket and starts the outbound pump.
func (r *edgeRelay) AttachLocal(pc net.PacketConn) {
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		return
	}
	r.local = pc
	r.mu.Unlock()

	r.wg.Add(1)
	go func() {
		defer r.wg.Done()
		r.pumpOutbound(pc)
	}()
}

// AttachTailnet installs the tailnet socket and starts the inbound pump.
func (r *edgeRelay) AttachTailnet(pc net.PacketConn, port int) {
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		_ = pc.Close()
		return
	}
	old := r.tailnet
	r.tailnet = pc
	r.tailnetPort = port
	r.mu.Unlock()

	if old != nil {
		_ = old.Close()
	}

	r.wg.Add(1)
	go func() {
		defer r.wg.Done()
		r.pumpInbound(pc)
	}()
}

// DetachTailnet closes the tailnet socket, ending the inbound pump.
func (r *edgeRelay) DetachTailnet() {
	r.mu.Lock()
	pc := r.tailnet
	r.tailnet = nil
	r.tailnetPort = 0
	r.mu.Unlock()

	if pc != nil {
		_ = pc.Close()
	}
}

// SetCallback records where inbound edges are delivered on loopback. Setting it
// explicitly disables source-address learning.
func (r *edgeRelay) SetCallback(addr *net.UDPAddr) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.callback = addr
	r.callbackLocked = true
}

// SetPeer records the tailnet endpoint that outbound edges are sent to, and the
// only source inbound edges are accepted from.
func (r *edgeRelay) SetPeer(ep netip.AddrPort) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.peer = ep
}

func (r *edgeRelay) Peer() netip.AddrPort {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.peer
}

func (r *edgeRelay) Close() {
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		return
	}
	r.closed = true
	tn := r.tailnet
	r.tailnet = nil
	r.mu.Unlock()

	if tn != nil {
		_ = tn.Close()
	}
	// The loopback socket is owned by main and closed there, which unblocks the
	// outbound pump.
}

// Wait blocks until both pumps have exited. Used by tests.
func (r *edgeRelay) Wait() { r.wg.Wait() }

func (r *edgeRelay) pumpOutbound(pc net.PacketConn) {
	buf := make([]byte, maxDatagram)
	for {
		n, src, err := pc.ReadFrom(buf)
		if err != nil {
			if !r.recoverableRead(err, "edge loopback") {
				return
			}
			continue
		}

		if ua, ok := src.(*net.UDPAddr); ok {
			r.observeSource(ua)
		}

		tn, peer := r.tailnetAndPeer()
		if tn == nil || !peer.IsValid() {
			r.stats.dropNoPeer.Add(1)
			continue
		}
		if _, err := tn.WriteTo(buf[:n], net.UDPAddrFromAddrPort(peer)); err != nil {
			r.stats.txErrors.Add(1)
			continue
		}
		r.stats.txDatagrams.Add(1)
		r.stats.txBytes.Add(uint64(n))
	}
}

func (r *edgeRelay) pumpInbound(pc net.PacketConn) {
	buf := make([]byte, maxDatagram)
	for {
		n, src, err := pc.ReadFrom(buf)
		if err != nil {
			if !r.recoverableRead(err, "edge tailnet") {
				return
			}
			continue
		}

		local, cb, peer := r.localCallbackPeer()
		if peer.IsValid() && !sourceMatches(src, peer.Addr()) {
			// Tailscale ACLs are the primary control; this is a cheap second
			// check so a datagram from an unexpected tailnet node cannot be
			// handed to the keying path.
			r.stats.dropForeign.Add(1)
			continue
		}
		if local == nil || cb == nil {
			r.stats.dropNoCallback.Add(1)
			continue
		}
		if _, err := local.WriteTo(buf[:n], cb); err != nil {
			r.stats.rxErrors.Add(1)
			continue
		}
		r.stats.rxDatagrams.Add(1)
		r.stats.rxBytes.Add(uint64(n))
	}
}

// recoverableRead reports whether the pump should keep going. A closed socket
// ends the pump; anything else is counted and retried, because Windows can
// surface transient errors such as WSAECONNRESET on unconnected UDP sockets.
func (r *edgeRelay) recoverableRead(err error, what string) bool {
	if errors.Is(err, net.ErrClosed) {
		return false
	}
	r.stats.rxErrors.Add(1)
	r.logf("%s read error: %v", what, err)
	// Small pause so a hard-failing socket cannot spin a core.
	time.Sleep(5 * time.Millisecond)
	return true
}

// observeSource learns the .NET receive endpoint from the source of an outbound
// datagram. Only used when no callback was configured explicitly, and only
// valid when the .NET side uses one socket for both directions.
func (r *edgeRelay) observeSource(ua *net.UDPAddr) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.callbackLocked || r.callback != nil {
		return
	}
	learned := &net.UDPAddr{IP: ua.IP, Port: ua.Port, Zone: ua.Zone}
	r.callback = learned
	r.logf("edge callback learned from outbound source %s (pass -edge-callback-addr to set it explicitly)", learned)
}

func (r *edgeRelay) tailnetAndPeer() (net.PacketConn, netip.AddrPort) {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.tailnet, r.peer
}

func (r *edgeRelay) localCallbackPeer() (net.PacketConn, *net.UDPAddr, netip.AddrPort) {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.local, r.callback, r.peer
}

func sourceMatches(src net.Addr, want netip.Addr) bool {
	ua, ok := src.(*net.UDPAddr)
	if !ok {
		return true // unknown address shape; leave the decision to Tailscale ACLs
	}
	got, ok := netip.AddrFromSlice(ua.IP)
	if !ok {
		return true
	}
	return got.Unmap() == want.Unmap()
}

func (r *edgeRelay) Snapshot() edgeStatus {
	r.mu.Lock()
	local, tailnet, cb, peer, port := r.local, r.tailnet, r.callback, r.peer, r.tailnetPort
	r.mu.Unlock()

	st := edgeStatus{
		Transport:      EdgeTransport,
		JitterProfile:  jitterProfilePathAdaptive,
		TailnetPort:    port,
		TxDatagrams:    r.stats.txDatagrams.Load(),
		TxBytes:        r.stats.txBytes.Load(),
		RxDatagrams:    r.stats.rxDatagrams.Load(),
		RxBytes:        r.stats.rxBytes.Load(),
		DropNoPeer:     r.stats.dropNoPeer.Load(),
		DropNoCallback: r.stats.dropNoCallback.Load(),
		DropForeign:    r.stats.dropForeign.Load(),
		TxErrors:       r.stats.txErrors.Load(),
		RxErrors:       r.stats.rxErrors.Load(),
	}
	if local != nil {
		st.LocalAddress = local.LocalAddr().String()
	}
	if tailnet != nil {
		st.TailnetAddress = tailnet.LocalAddr().String()
	}
	if cb != nil {
		st.CallbackAddress = cb.String()
	}
	if peer.IsValid() {
		st.PeerEndpoint = peer.String()
	}
	return st
}
