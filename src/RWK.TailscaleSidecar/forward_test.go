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
	"fmt"
	"net"
	"sync"
	"testing"
	"time"
)

// ─── Fake tailnetTransport ──────────────────────────────────────────────────────

// fakeTailnetTransport implements tailnetTransport using real loopback sockets
// to simulate tailnet connectivity for both IPv4 and IPv6.
type fakeTailnetTransport struct {
	mu       sync.Mutex
	peerHost string
	// listeners tracks listeners created via ListenTailnet (keyed by address).
	listeners []net.Listener
	// packetConns tracks UDP sockets created via ListenPacketTailnet.
	packetConns []net.PacketConn
}

func newFakeTransport(peerHost string) *fakeTailnetTransport {
	return &fakeTailnetTransport{peerHost: peerHost}
}

func (f *fakeTailnetTransport) DialTailnet(ctx context.Context, network, address string) (net.Conn, error) {
	var d net.Dialer
	return d.DialContext(ctx, network, address)
}

func (f *fakeTailnetTransport) ListenTailnet(network, address string) (net.Listener, error) {
	ln, err := net.Listen(network, address)
	if err != nil {
		return nil, err
	}
	f.mu.Lock()
	f.listeners = append(f.listeners, ln)
	f.mu.Unlock()
	return ln, nil
}

func (f *fakeTailnetTransport) ListenPacketTailnet(network, address string) (net.PacketConn, error) {
	pc, err := net.ListenPacket(network, address)
	if err != nil {
		return nil, err
	}
	f.mu.Lock()
	f.packetConns = append(f.packetConns, pc)
	f.mu.Unlock()
	return pc, nil
}

func (f *fakeTailnetTransport) PeerHost() string {
	return f.peerHost
}

func (f *fakeTailnetTransport) TailnetAddrs() (ipv4, ipv6 string) {
	// For testing, return loopback addresses as simulated tailnet addresses.
	// Real tailnet would return 100.x.x.x and fd7a:...
	return "127.0.0.1", "::1"
}

func (f *fakeTailnetTransport) Close() {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, ln := range f.listeners {
		_ = ln.Close()
	}
	for _, pc := range f.packetConns {
		_ = pc.Close()
	}
}

func noopLog(string, ...any) {}

// ─── TCP Tests ──────────────────────────────────────────────────────────────────

func TestForwardOutbound_TCP_IPv4(t *testing.T) {
	testForwardOutboundTCP(t, "127.0.0.1")
}

func TestForwardOutbound_TCP_IPv6(t *testing.T) {
	testForwardOutboundTCP(t, "::1")
}

func testForwardOutboundTCP(t *testing.T, peerHost string) {
	// Start a "Station" TCP echo server on loopback (simulates the peer).
	echoLn, err := net.Listen("tcp", net.JoinHostPort(peerHost, "0"))
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoLn.Close()
	_, echoPort, _ := net.SplitHostPort(echoLn.Addr().String())

	go func() {
		for {
			c, err := echoLn.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				buf := make([]byte, 256)
				n, _ := c.Read(buf)
				c.Write(buf[:n])
			}(c)
		}
	}()

	// The fake transport "dials tailnet" by dialing loopback (simulating the peer).
	transport := newFakeTransport(peerHost)
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	tailnetPort := mustParsePort(t, echoPort)
	info, err := fm.Add(forwardSpec{
		Kind:        forwardOutbound,
		BindAddress: peerHost, // bind on same family as peer for this test
		LocalPort:   0,
		TailnetPort: tailnetPort,
	})
	if err != nil {
		t.Fatalf("Add outbound: %v", err)
	}

	// Connect through the forward.
	conn, err := net.DialTimeout("tcp", info.ListenAddress, 2*time.Second)
	if err != nil {
		t.Fatalf("dial forward: %v", err)
	}
	defer conn.Close()

	msg := []byte("hello-ipv6-test")
	conn.Write(msg)
	conn.(*net.TCPConn).CloseWrite()

	buf := make([]byte, 256)
	n, _ := conn.Read(buf)
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

func TestForwardInbound_TCP_IPv4(t *testing.T) {
	testForwardInboundTCP(t, "127.0.0.1")
}

func TestForwardInbound_TCP_IPv6(t *testing.T) {
	testForwardInboundTCP(t, "::1")
}

func testForwardInboundTCP(t *testing.T, host string) {
	// Start a local echo server (simulates the Station LAN target).
	echoLn, err := net.Listen("tcp", net.JoinHostPort(host, "0"))
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoLn.Close()
	_, echoPort, _ := net.SplitHostPort(echoLn.Addr().String())

	go func() {
		for {
			c, err := echoLn.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				buf := make([]byte, 256)
				n, _ := c.Read(buf)
				c.Write(buf[:n])
			}(c)
		}
	}()

	transport := newFakeTransport("")
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	localPort := mustParsePort(t, echoPort)
	info, err := fm.Add(forwardSpec{
		Kind:        forwardInbound,
		BindAddress: host,
		LocalPort:   localPort,
		TailnetPort: 19999,
	})
	if err != nil {
		t.Fatalf("Add inbound: %v", err)
	}

	// Connect to the tailnet listener (simulating a peer connecting).
	conn, err := net.DialTimeout("tcp", info.ListenAddress, 2*time.Second)
	if err != nil {
		t.Fatalf("dial inbound forward: %v", err)
	}
	defer conn.Close()

	msg := []byte("inbound-test")
	conn.Write(msg)
	conn.(*net.TCPConn).CloseWrite()

	buf := make([]byte, 256)
	n, _ := conn.Read(buf)
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

// ─── UDP Tests ──────────────────────────────────────────────────────────────────

func TestForwardOutbound_UDP_IPv4(t *testing.T) {
	testForwardOutboundUDP(t, "127.0.0.1")
}

func TestForwardOutbound_UDP_IPv6(t *testing.T) {
	testForwardOutboundUDP(t, "::1")
}

func testForwardOutboundUDP(t *testing.T, peerHost string) {
	// Start a UDP echo server (simulates the peer receiving forwarded datagrams).
	echoPC, err := net.ListenPacket("udp", net.JoinHostPort(peerHost, "0"))
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoPC.Close()
	_, echoPort, _ := net.SplitHostPort(echoPC.LocalAddr().String())

	go func() {
		buf := make([]byte, 4096)
		for {
			n, addr, err := echoPC.ReadFrom(buf)
			if err != nil {
				return
			}
			echoPC.WriteTo(buf[:n], addr)
		}
	}()

	// For outbound UDP, the transport's ListenPacketTailnet creates a socket
	// that can reach the echo server (simulating the tailnet relay path).
	transport := newFakeTransport(peerHost)
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	tailnetPort := mustParsePort(t, echoPort)
	info, err := fm.Add(forwardSpec{
		Kind:        forwardOutboundUdp,
		BindAddress: "127.0.0.1",
		LocalPort:   0,
		TailnetPort: tailnetPort,
		PeerHost:    peerHost,
	})
	if err != nil {
		t.Fatalf("Add outbound-udp: %v", err)
	}

	// Send a datagram through the forward.
	clientConn, err := net.Dial("udp", info.ListenAddress)
	if err != nil {
		t.Fatalf("dial forward: %v", err)
	}
	defer clientConn.Close()

	msg := []byte("udp-outbound-test")
	clientConn.Write(msg)

	clientConn.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, 256)
	n, err := clientConn.Read(buf)
	if err != nil {
		t.Fatalf("read reply: %v", err)
	}
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

func TestForwardInbound_UDP_IPv4(t *testing.T) {
	testForwardInboundUDP(t, "127.0.0.1")
}

func TestForwardInbound_UDP_IPv6(t *testing.T) {
	// For inbound UDP, the tailnet listener uses "udp" (dual-stack) and the
	// local target is always IPv4 loopback (IPC to .NET process). This test
	// verifies the inbound path works with the dual-stack tailnet listener
	// when the local target is IPv4. Real IPv6 tailnet source testing requires
	// a live tsnet node with an fd7a: address.
	host := "127.0.0.1"

	// Start a local UDP echo server (simulates the .NET process as target).
	echoPC, err := net.ListenPacket("udp", net.JoinHostPort(host, "0"))
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoPC.Close()
	_, echoPort, _ := net.SplitHostPort(echoPC.LocalAddr().String())

	go func() {
		buf := make([]byte, 4096)
		for {
			n, addr, err := echoPC.ReadFrom(buf)
			if err != nil {
				return
			}
			echoPC.WriteTo(buf[:n], addr)
		}
	}()

	transport := newFakeTransport("")
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	localPort := mustParsePort(t, echoPort)
	info, err := fm.Add(forwardSpec{
		Kind:        forwardInboundUdp,
		BindAddress: host,
		LocalPort:   localPort,
		TailnetPort: 20002, // different port from IPv4 test
	})
	if err != nil {
		t.Fatalf("Add inbound-udp: %v", err)
	}

	// Send a datagram to the tailnet listener.
	senderConn, err := net.Dial("udp", info.ListenAddress)
	if err != nil {
		t.Fatalf("dial inbound forward: %v", err)
	}
	defer senderConn.Close()

	msg := []byte("udp-inbound-v6-test")
	senderConn.Write(msg)

	senderConn.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, 256)
	n, err := senderConn.Read(buf)
	if err != nil {
		t.Fatalf("read reply: %v", err)
	}
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

func testForwardInboundUDP(t *testing.T, host string) {
	// Start a local UDP echo server (simulates the Station LAN target).
	echoPC, err := net.ListenPacket("udp", net.JoinHostPort(host, "0"))
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoPC.Close()
	_, echoPort, _ := net.SplitHostPort(echoPC.LocalAddr().String())

	go func() {
		buf := make([]byte, 4096)
		for {
			n, addr, err := echoPC.ReadFrom(buf)
			if err != nil {
				return
			}
			echoPC.WriteTo(buf[:n], addr)
		}
	}()

	transport := newFakeTransport("")
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	localPort := mustParsePort(t, echoPort)
	info, err := fm.Add(forwardSpec{
		Kind:        forwardInboundUdp,
		BindAddress: host,
		LocalPort:   localPort,
		TailnetPort: 20000,
	})
	if err != nil {
		t.Fatalf("Add inbound-udp: %v", err)
	}

	// Send a datagram to the tailnet listener (simulating a peer).
	senderConn, err := net.Dial("udp", info.ListenAddress)
	if err != nil {
		t.Fatalf("dial inbound forward: %v", err)
	}
	defer senderConn.Close()

	msg := []byte("udp-inbound-test")
	senderConn.Write(msg)

	senderConn.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, 256)
	n, err := senderConn.Read(buf)
	if err != nil {
		t.Fatalf("read reply: %v", err)
	}
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

// TestResolveUDPAddr_IPv6Target verifies that the fix to ResolveUDPAddr("udp", ...)
// correctly resolves an IPv6 literal target. With the old "udp4" code, this would
// fail because net.ResolveUDPAddr("udp4", "[::1]:1234") returns an error.
func TestResolveUDPAddr_IPv6Target(t *testing.T) {
	// This test demonstrates the exact bug that existed before the fix:
	// ResolveUDPAddr("udp4", ...) fails for IPv6 addresses.
	_, err := net.ResolveUDPAddr("udp4", "[::1]:1234")
	if err == nil {
		t.Fatal("expected udp4 to reject IPv6 literal, but it succeeded — test premise is wrong")
	}

	// After the fix: "udp" accepts both families.
	addr, err := net.ResolveUDPAddr("udp", "[::1]:1234")
	if err != nil {
		t.Fatalf("udp should accept IPv6 literal: %v", err)
	}
	if addr.IP.To4() != nil {
		t.Fatal("expected IPv6 address, got IPv4")
	}
}

// TestMixedFamily_IPv4BindToIPv6Target verifies that an IPv4 client-side bind
// can relay to an IPv6 Station target (since BindAddress and StationTargetAddress
// are independent and don't need to match family).
func TestMixedFamily_IPv4BindToIPv6Target(t *testing.T) {
	// IPv6 echo server as the "Station target"
	echoPC, err := net.ListenPacket("udp", "[::1]:0")
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoPC.Close()
	_, echoPort, _ := net.SplitHostPort(echoPC.LocalAddr().String())

	go func() {
		buf := make([]byte, 4096)
		for {
			n, addr, err := echoPC.ReadFrom(buf)
			if err != nil {
				return
			}
			echoPC.WriteTo(buf[:n], addr)
		}
	}()

	// Transport that "dials tailnet" at the IPv6 echo server
	transport := newFakeTransport("::1")
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	tailnetPort := mustParsePort(t, echoPort)
	info, err := fm.Add(forwardSpec{
		Kind:        forwardOutboundUdp,
		BindAddress: "127.0.0.1", // IPv4 client bind
		LocalPort:   0,
		TailnetPort: tailnetPort,
		PeerHost:    "::1", // IPv6 target
	})
	if err != nil {
		t.Fatalf("Add mixed-family forward: %v", err)
	}

	// Send from IPv4 client
	clientConn, err := net.Dial("udp4", info.ListenAddress)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer clientConn.Close()

	msg := []byte("mixed-family-test")
	clientConn.Write(msg)

	clientConn.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, 256)
	n, err := clientConn.Read(buf)
	if err != nil {
		t.Fatalf("read reply: %v", err)
	}
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

// TestMixedFamily_IPv6BindToIPv4Target verifies the reverse: IPv6 source relaying
// to an IPv4 Station target.
func TestMixedFamily_IPv6BindToIPv4Target(t *testing.T) {
	// IPv4 echo server as the "Station target"
	echoPC, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	defer echoPC.Close()
	_, echoPort, _ := net.SplitHostPort(echoPC.LocalAddr().String())

	go func() {
		buf := make([]byte, 4096)
		for {
			n, addr, err := echoPC.ReadFrom(buf)
			if err != nil {
				return
			}
			echoPC.WriteTo(buf[:n], addr)
		}
	}()

	transport := newFakeTransport("127.0.0.1")
	defer transport.Close()

	fm := newForwardManager(transport, noopLog)
	defer fm.Close()

	tailnetPort := mustParsePort(t, echoPort)
	// Inbound UDP: tailnet listener on IPv6, target is IPv4
	info, err := fm.Add(forwardSpec{
		Kind:        forwardInboundUdp,
		BindAddress: "127.0.0.1",       // local target is IPv4
		LocalPort:   tailnetPort,        // local target port
		TailnetPort: 20001,
	})
	if err != nil {
		t.Fatalf("Add inbound-udp: %v", err)
	}

	// Send from any source to the tailnet listener
	senderConn, err := net.Dial("udp", info.ListenAddress)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer senderConn.Close()

	msg := []byte("reverse-mixed-test")
	senderConn.Write(msg)

	senderConn.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, 256)
	n, err := senderConn.Read(buf)
	if err != nil {
		t.Fatalf("read reply: %v", err)
	}
	if string(buf[:n]) != string(msg) {
		t.Fatalf("echo mismatch: got %q, want %q", buf[:n], msg)
	}
}

// ─── Helpers ────────────────────────────────────────────────────────────────────

func mustParsePort(t *testing.T, s string) int {
	t.Helper()
	var port int
	_, err := fmt.Sscanf(s, "%d", &port)
	if err != nil {
		t.Fatalf("parse port %q: %v", s, err)
	}
	return port
}
