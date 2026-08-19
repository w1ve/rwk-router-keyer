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
	"io"
	"net"
	"sync"
	"sync/atomic"
	"time"
)

// tailnetTransport is the slice of the tsnet node that forwarding needs.
// Keeping it an interface lets the forward logic be tested without a tailnet.
type tailnetTransport interface {
	// DialTailnet opens a connection to an address on the tailnet.
	DialTailnet(ctx context.Context, network, address string) (net.Conn, error)
	// ListenTailnet accepts connections arriving over the tailnet.
	ListenTailnet(network, address string) (net.Listener, error)
	// ListenPacketTailnet binds a UDP socket on the tailnet.
	ListenPacketTailnet(network, address string) (net.PacketConn, error)
	// PeerHost returns the currently configured peer address, or "" if none.
	PeerHost() string
}

type forwardKind string

const (
	// forwardOutbound listens on loopback and dials the peer over the tailnet.
	// This is how ConnectControlAsync reaches the Station's control port (5.7):
	// the .NET side makes an ordinary TcpClient connection to loopback.
	forwardOutbound forwardKind = "out"
	// forwardInbound listens on the tailnet and dials a loopback port. This is
	// how the Station's SessionManager receives control connections, since the
	// tailnet stack lives in this process rather than in the .NET process.
	forwardInbound forwardKind = "in"
	// forwardOutboundUdp binds a loopback UDP socket and relays datagrams to the
	// peer over the tailnet. Replies from the peer are relayed back.
	forwardOutboundUdp forwardKind = "out-udp"
	// forwardInboundUdp binds a tailnet UDP socket and relays datagrams to a local
	// target. Replies from the target are relayed back.
	forwardInboundUdp forwardKind = "in-udp"
)

// forwardSpec is the request body for POST /v1/forwards.
type forwardSpec struct {
	Kind forwardKind `json:"kind"`
	// BindAddress is the loopback address for an outbound forward.
	// Defaults to 127.0.0.1. Deliberately not defaulted to any-address.
	BindAddress string `json:"bindAddress,omitempty"`
	// LocalPort is the loopback port: bound for an outbound forward (0 selects
	// a free port), dialed for an inbound forward (required).
	LocalPort int `json:"localPort,omitempty"`
	// TailnetPort is the peer port for an outbound forward, or the port bound
	// on the tailnet for an inbound forward.
	TailnetPort int `json:"tailnetPort"`
	// PeerHost overrides the configured peer for an outbound forward.
	PeerHost string `json:"peerHost,omitempty"`
}

// forwardInfo is the JSON view of an active forward.
type forwardInfo struct {
	ID            string      `json:"id"`
	Kind          forwardKind `json:"kind"`
	ListenAddress string      `json:"listenAddress"`
	Target        string      `json:"target"`
	Accepted      uint64      `json:"accepted"`
	Active        int64       `json:"active"`
	Errors        uint64      `json:"errors"`
}

type forwardEntry struct {
	id     string
	spec   forwardSpec
	listen string
	target string
	ln     net.Listener
	// UDP forward fields (nil for TCP forwards)
	udp *udpForward

	accepted atomic.Uint64
	active   atomic.Int64
	errs     atomic.Uint64
}

type forwardManager struct {
	logf      func(string, ...any)
	transport tailnetTransport

	mu     sync.Mutex
	items  map[string]*forwardEntry
	nextID int
	closed bool
	wg     sync.WaitGroup
}

func newForwardManager(transport tailnetTransport, logf func(string, ...any)) *forwardManager {
	return &forwardManager{
		logf:      logf,
		transport: transport,
		items:     make(map[string]*forwardEntry),
	}
}

var errForwardClosed = errors.New("forward manager closed")

func (m *forwardManager) Add(spec forwardSpec) (forwardInfo, error) {
	if spec.BindAddress == "" {
		spec.BindAddress = "127.0.0.1"
	}

	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		return forwardInfo{}, errForwardClosed
	}
	m.nextID++
	id := fmt.Sprintf("fwd-%d", m.nextID)
	m.mu.Unlock()

	entry := &forwardEntry{id: id, spec: spec}

	switch spec.Kind {
	case forwardOutbound:
		if spec.TailnetPort <= 0 || spec.TailnetPort > 65535 {
			return forwardInfo{}, fmt.Errorf("outbound forward needs a tailnetPort in 1..65535, got %d", spec.TailnetPort)
		}
		peer := spec.PeerHost
		if peer == "" {
			peer = m.transport.PeerHost()
		}
		if peer == "" {
			return forwardInfo{}, errors.New("outbound forward needs peerHost, or a peer must be configured first")
		}
		ln, err := net.Listen("tcp", net.JoinHostPort(spec.BindAddress, itoa(spec.LocalPort)))
		if err != nil {
			return forwardInfo{}, fmt.Errorf("bind %s: %w", spec.BindAddress, err)
		}
		entry.ln = ln
		entry.listen = ln.Addr().String()
		entry.target = net.JoinHostPort(peer, itoa(spec.TailnetPort))

	case forwardInbound:
		if spec.TailnetPort <= 0 || spec.TailnetPort > 65535 {
			return forwardInfo{}, fmt.Errorf("inbound forward needs a tailnetPort in 1..65535, got %d", spec.TailnetPort)
		}
		if spec.LocalPort <= 0 || spec.LocalPort > 65535 {
			return forwardInfo{}, fmt.Errorf("inbound forward needs a localPort in 1..65535, got %d", spec.LocalPort)
		}
		ln, err := m.transport.ListenTailnet("tcp", ":"+itoa(spec.TailnetPort))
		if err != nil {
			return forwardInfo{}, fmt.Errorf("listen on tailnet port %d: %w", spec.TailnetPort, err)
		}
		entry.ln = ln
		entry.listen = ln.Addr().String()
		entry.target = net.JoinHostPort(spec.BindAddress, itoa(spec.LocalPort))

	case forwardOutboundUdp:
		if spec.TailnetPort <= 0 || spec.TailnetPort > 65535 {
			return forwardInfo{}, fmt.Errorf("outbound-udp forward needs a tailnetPort in 1..65535, got %d", spec.TailnetPort)
		}
		peer := spec.PeerHost
		if peer == "" {
			peer = m.transport.PeerHost()
		}
		if peer == "" {
			return forwardInfo{}, errors.New("outbound-udp forward needs peerHost, or a peer must be configured first")
		}
		// Bind loopback UDP to receive from .NET
		localAddr := net.JoinHostPort(spec.BindAddress, itoa(spec.LocalPort))
		localPC, err := net.ListenPacket("udp4", localAddr)
		if err != nil {
			return forwardInfo{}, fmt.Errorf("bind udp %s: %w", localAddr, err)
		}
		// Dial the peer over tailnet UDP (tsnet supports udp in Dial)
		peerAddr := net.JoinHostPort(peer, itoa(spec.TailnetPort))
		uf := newUdpForward(localPC, nil, peerAddr, m.transport, m.logf)
		entry.udp = uf
		entry.listen = localPC.LocalAddr().String()
		entry.target = peerAddr

	case forwardInboundUdp:
		if spec.TailnetPort <= 0 || spec.TailnetPort > 65535 {
			return forwardInfo{}, fmt.Errorf("inbound-udp forward needs a tailnetPort in 1..65535, got %d", spec.TailnetPort)
		}
		if spec.LocalPort <= 0 || spec.LocalPort > 65535 {
			return forwardInfo{}, fmt.Errorf("inbound-udp forward needs a localPort in 1..65535, got %d", spec.LocalPort)
		}
		// Bind tailnet UDP to receive from peer
		tailnetPC, err := m.transport.ListenPacketTailnet("udp4", ":"+itoa(spec.TailnetPort))
		if err != nil {
			return forwardInfo{}, fmt.Errorf("listen udp on tailnet port %d: %w", spec.TailnetPort, err)
		}
		// Target is the local address (Station LAN device or localhost)
		targetAddr := net.JoinHostPort(spec.BindAddress, itoa(spec.LocalPort))
		uf := newUdpForward(nil, tailnetPC, targetAddr, nil, m.logf)
		entry.udp = uf
		entry.listen = tailnetPC.LocalAddr().String()
		entry.target = targetAddr

	default:
		return forwardInfo{}, fmt.Errorf("unknown forward kind %q (want %q, %q, %q, or %q)", spec.Kind, forwardOutbound, forwardInbound, forwardOutboundUdp, forwardInboundUdp)
	}

	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		if entry.ln != nil {
			_ = entry.ln.Close()
		}
		if entry.udp != nil {
			entry.udp.Close()
		}
		return forwardInfo{}, errForwardClosed
	}
	m.items[id] = entry
	m.mu.Unlock()

	m.wg.Add(1)
	go func() {
		defer m.wg.Done()
		if entry.udp != nil {
			entry.udp.Run()
		} else {
			m.accept(entry)
		}
	}()

	m.logf("forward %s (%s) listening on %s -> %s", id, spec.Kind, entry.listen, entry.target)
	return entry.info(), nil
}

func (m *forwardManager) accept(e *forwardEntry) {
	for {
		conn, err := e.ln.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			e.errs.Add(1)
			m.logf("forward %s accept error: %v", e.id, err)
			time.Sleep(20 * time.Millisecond)
			continue
		}
		e.accepted.Add(1)
		e.active.Add(1)

		go func(in net.Conn) {
			defer e.active.Add(-1)
			defer in.Close()

			ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
			defer cancel()

			var out net.Conn
			var derr error
			if e.spec.Kind == forwardOutbound {
				out, derr = m.transport.DialTailnet(ctx, "tcp", e.target)
			} else {
				d := net.Dialer{}
				out, derr = d.DialContext(ctx, "tcp", e.target)
			}
			if derr != nil {
				e.errs.Add(1)
				m.logf("forward %s dial %s failed: %v", e.id, e.target, derr)
				return
			}
			defer out.Close()

			// The control channel is latency sensitive and low volume, so Nagle
			// must not batch small writes.
			setNoDelay(in)
			setNoDelay(out)

			pipe(in, out)
		}(conn)
	}
}

func (m *forwardManager) Remove(id string) bool {
	m.mu.Lock()
	e, ok := m.items[id]
	if ok {
		delete(m.items, id)
	}
	m.mu.Unlock()

	if !ok {
		return false
	}
	if e.ln != nil {
		_ = e.ln.Close()
	}
	if e.udp != nil {
		e.udp.Close()
	}
	m.logf("forward %s removed", id)
	return true
}

func (m *forwardManager) List() []forwardInfo {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]forwardInfo, 0, len(m.items))
	for _, e := range m.items {
		out = append(out, e.info())
	}
	return out
}

func (m *forwardManager) Close() {
	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		return
	}
	m.closed = true
	items := m.items
	m.items = make(map[string]*forwardEntry)
	m.mu.Unlock()

	for _, e := range items {
		if e.ln != nil {
			_ = e.ln.Close()
		}
		if e.udp != nil {
			e.udp.Close()
		}
	}
	m.wg.Wait()
}

// CloseTailnetForwards drops forwards that depend on the tailnet stack, which
// must happen before the tsnet server closes.
func (m *forwardManager) CloseTailnetForwards() {
	m.mu.Lock()
	var doomed []*forwardEntry
	for id, e := range m.items {
		if e.spec.Kind == forwardInbound || e.spec.Kind == forwardInboundUdp {
			doomed = append(doomed, e)
			delete(m.items, id)
		}
	}
	m.mu.Unlock()

	for _, e := range doomed {
		if e.ln != nil {
			_ = e.ln.Close()
		}
		if e.udp != nil {
			e.udp.Close()
		}
	}
}

func (e *forwardEntry) info() forwardInfo {
	return forwardInfo{
		ID:            e.id,
		Kind:          e.spec.Kind,
		ListenAddress: e.listen,
		Target:        e.target,
		Accepted:      e.accepted.Load(),
		Active:        e.active.Load(),
		Errors:        e.errs.Load(),
	}
}

func setNoDelay(c net.Conn) {
	if tc, ok := c.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
}

// pipe copies in both directions and propagates half-close, so a FIN in one
// direction closes only that direction.
func pipe(a, b net.Conn) {
	done := make(chan struct{}, 2)
	go func() { copyHalf(b, a); done <- struct{}{} }()
	go func() { copyHalf(a, b); done <- struct{}{} }()
	<-done
	<-done
}

func copyHalf(dst, src net.Conn) {
	_, _ = io.Copy(dst, src)
	if cw, ok := dst.(interface{ CloseWrite() error }); ok {
		_ = cw.CloseWrite()
	}
}


// ─── UDP Forward ──────────────────────────────────────────────────────────────

const udpForwardMaxDatagram = 4096
const udpForwardIdleTimeout = 60 * time.Second

// udpForward relays UDP datagrams between a local socket and a remote target.
// For outbound-udp: local=loopback (receives from .NET), remote=tailnet peer.
// For inbound-udp: local=tailnet (receives from peer), remote=local target.
type udpForward struct {
	logf      func(string, ...any)
	local     net.PacketConn // receives datagrams from the source side
	transport tailnetTransport
	target    string // address to forward to (resolved at relay time for outbound)

	mu       sync.Mutex
	sessions map[string]*udpFwdSession
	closed   bool
	wg       sync.WaitGroup
}

type udpFwdSession struct {
	sender   net.Addr
	socket   net.PacketConn
	lastSeen time.Time
}

func newUdpForward(local, tailnet net.PacketConn, target string, transport tailnetTransport, logf func(string, ...any)) *udpForward {
	pc := local
	if pc == nil {
		pc = tailnet
	}
	return &udpForward{
		logf:      logf,
		local:     pc,
		transport: transport,
		target:    target,
		sessions:  make(map[string]*udpFwdSession),
	}
}

// Run starts the relay. Blocks until Close is called.
func (u *udpForward) Run() {
	// Scavenge idle sessions periodically.
	u.wg.Add(1)
	go func() {
		defer u.wg.Done()
		u.scavengeLoop()
	}()

	buf := make([]byte, udpForwardMaxDatagram)
	for {
		n, sender, err := u.local.ReadFrom(buf)
		if err != nil {
			if u.isClosed() {
				return
			}
			if errors.Is(err, net.ErrClosed) {
				return
			}
			u.logf("udp-fwd read error: %v", err)
			time.Sleep(5 * time.Millisecond)
			continue
		}

		session := u.getOrCreateSession(sender)
		if session == nil {
			continue
		}
		session.lastSeen = time.Now()

		// Forward datagram to target.
		targetAddr, err := net.ResolveUDPAddr("udp4", u.target)
		if err != nil {
			u.logf("udp-fwd resolve target %s: %v", u.target, err)
			continue
		}
		if _, err := session.socket.WriteTo(buf[:n], targetAddr); err != nil {
			u.logf("udp-fwd write to %s: %v", u.target, err)
		}
	}
}

func (u *udpForward) getOrCreateSession(sender net.Addr) *udpFwdSession {
	key := sender.String()

	u.mu.Lock()
	if u.closed {
		u.mu.Unlock()
		return nil
	}
	sess, ok := u.sessions[key]
	if ok {
		u.mu.Unlock()
		return sess
	}

	// Create a new session: bind a socket to relay replies back to the sender.
	var pc net.PacketConn
	var err error
	if u.transport != nil {
		// Outbound: use tailnet UDP to reach the peer.
		pc, err = u.transport.ListenPacketTailnet("udp4", ":0")
	} else {
		// Inbound: use a regular loopback socket to reach the local target.
		pc, err = net.ListenPacket("udp4", "127.0.0.1:0")
	}
	if err != nil {
		u.mu.Unlock()
		u.logf("udp-fwd session socket error for %s: %v", key, err)
		return nil
	}

	sess = &udpFwdSession{sender: sender, socket: pc, lastSeen: time.Now()}
	u.sessions[key] = sess
	u.mu.Unlock()

	// Start reply pump for this session.
	u.wg.Add(1)
	go func() {
		defer u.wg.Done()
		u.replyPump(sess)
	}()

	return sess
}

// replyPump reads replies from the session socket and forwards them back to the original sender.
func (u *udpForward) replyPump(sess *udpFwdSession) {
	buf := make([]byte, udpForwardMaxDatagram)
	for {
		_ = sess.socket.SetReadDeadline(time.Now().Add(udpForwardIdleTimeout))
		n, _, err := sess.socket.ReadFrom(buf)
		if err != nil {
			if u.isClosed() {
				return
			}
			if errors.Is(err, net.ErrClosed) {
				return
			}
			// Timeout or other error — session idle, let scavenger clean up.
			return
		}
		sess.lastSeen = time.Now()

		// Forward reply back to the original sender via the local socket.
		if _, err := u.local.WriteTo(buf[:n], sess.sender); err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			u.logf("udp-fwd reply to %s: %v", sess.sender, err)
		}
	}
}

func (u *udpForward) scavengeLoop() {
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	for {
		<-ticker.C
		if u.isClosed() {
			return
		}
		cutoff := time.Now().Add(-udpForwardIdleTimeout)
		u.mu.Lock()
		for key, sess := range u.sessions {
			if sess.lastSeen.Before(cutoff) {
				_ = sess.socket.Close()
				delete(u.sessions, key)
			}
		}
		u.mu.Unlock()
	}
}

func (u *udpForward) isClosed() bool {
	u.mu.Lock()
	defer u.mu.Unlock()
	return u.closed
}

func (u *udpForward) Close() {
	u.mu.Lock()
	if u.closed {
		u.mu.Unlock()
		return
	}
	u.closed = true
	sessions := u.sessions
	u.sessions = make(map[string]*udpFwdSession)
	u.mu.Unlock()

	// Close all session sockets.
	for _, sess := range sessions {
		_ = sess.socket.Close()
	}
	// Close the main local socket to unblock ReadFrom.
	_ = u.local.Close()

	u.wg.Wait()
}
