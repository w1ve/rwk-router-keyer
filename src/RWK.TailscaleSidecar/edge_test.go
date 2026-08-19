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
	"net"
	"net/netip"
	"testing"
	"time"
)

// The edge relay is exercised with real loopback UDP sockets standing in for the
// tsnet PacketConn and the remote node. No mocking: these are genuine
// datagrams through the genuine relay code.

func mustListenUDP(t *testing.T) net.PacketConn {
	t.Helper()
	pc, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	t.Cleanup(func() { _ = pc.Close() })
	return pc
}

// portOfConn is the local UDP port as an int, which is what AttachTailnet takes.
func portOfConn(t *testing.T, pc net.PacketConn) int {
	t.Helper()
	return int(addrPortOf(t, pc).Port())
}

func addrPortOf(t *testing.T, pc net.PacketConn) netip.AddrPort {
	t.Helper()
	ap, err := netip.ParseAddrPort(pc.LocalAddr().String())
	if err != nil {
		t.Fatalf("parse %q: %v", pc.LocalAddr(), err)
	}
	return ap
}

func readWithin(t *testing.T, pc net.PacketConn, d time.Duration) []byte {
	t.Helper()
	_ = pc.SetReadDeadline(time.Now().Add(d))
	buf := make([]byte, maxDatagram)
	n, _, err := pc.ReadFrom(buf)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	return buf[:n]
}

func TestEdgeRelayOutbound(t *testing.T) {
	relay := newEdgeRelay(func(string, ...any) {})
	local := mustListenUDP(t)
	tailnet := mustListenUDP(t)
	peer := mustListenUDP(t)
	sender := mustListenUDP(t)

	relay.AttachLocal(local)
	relay.AttachTailnet(tailnet, portOfConn(t, tailnet))
	relay.SetPeer(addrPortOf(t, peer))
	t.Cleanup(relay.Close)

	frame := []byte{0x52, 0x57, 0x4b, 0x50, 0x01, 0x00, 0x02, 0x00}
	if _, err := sender.WriteTo(frame, local.LocalAddr()); err != nil {
		t.Fatalf("write: %v", err)
	}

	got := readWithin(t, peer, 2*time.Second)
	if string(got) != string(frame) {
		t.Fatalf("peer received % x, want % x", got, frame)
	}
	if n := relay.Snapshot().TxDatagrams; n != 1 {
		t.Fatalf("txDatagrams = %d, want 1", n)
	}
}

func TestEdgeRelayInbound(t *testing.T) {
	relay := newEdgeRelay(func(string, ...any) {})
	local := mustListenUDP(t)
	tailnet := mustListenUDP(t)
	peer := mustListenUDP(t)
	callback := mustListenUDP(t)

	relay.AttachLocal(local)
	relay.AttachTailnet(tailnet, portOfConn(t, tailnet))
	relay.SetPeer(addrPortOf(t, peer))
	cbAddr, err := net.ResolveUDPAddr("udp4", callback.LocalAddr().String())
	if err != nil {
		t.Fatalf("resolve callback: %v", err)
	}
	relay.SetCallback(cbAddr)
	t.Cleanup(relay.Close)

	frame := []byte("edge-inbound")
	if _, err := peer.WriteTo(frame, tailnet.LocalAddr()); err != nil {
		t.Fatalf("write: %v", err)
	}

	got := readWithin(t, callback, 2*time.Second)
	if string(got) != string(frame) {
		t.Fatalf("callback received %q, want %q", got, frame)
	}
	if n := relay.Snapshot().RxDatagrams; n != 1 {
		t.Fatalf("rxDatagrams = %d, want 1", n)
	}
}

func TestEdgeRelayDropsWithoutPeer(t *testing.T) {
	relay := newEdgeRelay(func(string, ...any) {})
	local := mustListenUDP(t)
	tailnet := mustListenUDP(t)
	sender := mustListenUDP(t)

	relay.AttachLocal(local)
	relay.AttachTailnet(tailnet, portOfConn(t, tailnet))
	t.Cleanup(relay.Close)

	if _, err := sender.WriteTo([]byte("no-peer"), local.LocalAddr()); err != nil {
		t.Fatalf("write: %v", err)
	}

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if relay.Snapshot().DropNoPeer == 1 {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("expected dropNoPeer to reach 1, got %d", relay.Snapshot().DropNoPeer)
}

func TestEdgeRelayLearnsCallbackFromSource(t *testing.T) {
	relay := newEdgeRelay(func(string, ...any) {})
	local := mustListenUDP(t)
	tailnet := mustListenUDP(t)
	peer := mustListenUDP(t)
	app := mustListenUDP(t) // one socket used for both directions

	relay.AttachLocal(local)
	relay.AttachTailnet(tailnet, portOfConn(t, tailnet))
	relay.SetPeer(addrPortOf(t, peer))
	t.Cleanup(relay.Close)

	if _, err := app.WriteTo([]byte("out"), local.LocalAddr()); err != nil {
		t.Fatalf("write: %v", err)
	}
	readWithin(t, peer, 2*time.Second)

	if _, err := peer.WriteTo([]byte("back"), tailnet.LocalAddr()); err != nil {
		t.Fatalf("write: %v", err)
	}
	if got := string(readWithin(t, app, 2*time.Second)); got != "back" {
		t.Fatalf("app received %q, want %q", got, "back")
	}
}

func TestEdgeRelayRejectsForeignSource(t *testing.T) {
	relay := newEdgeRelay(func(string, ...any) {})
	local := mustListenUDP(t)
	tailnet := mustListenUDP(t)
	callback := mustListenUDP(t)
	intruder := mustListenUDP(t)

	relay.AttachLocal(local)
	relay.AttachTailnet(tailnet, portOfConn(t, tailnet))
	cbAddr, err := net.ResolveUDPAddr("udp4", callback.LocalAddr().String())
	if err != nil {
		t.Fatalf("resolve callback: %v", err)
	}
	relay.SetCallback(cbAddr)
	// Peer is on a different address than the intruder, which is loopback.
	relay.SetPeer(netip.MustParseAddrPort("100.64.0.9:41000"))
	t.Cleanup(relay.Close)

	if _, err := intruder.WriteTo([]byte("spoof"), tailnet.LocalAddr()); err != nil {
		t.Fatalf("write: %v", err)
	}

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if relay.Snapshot().DropForeign == 1 {
			if n := relay.Snapshot().RxDatagrams; n != 0 {
				t.Fatalf("rxDatagrams = %d, want 0", n)
			}
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("expected dropForeign to reach 1, got %d", relay.Snapshot().DropForeign)
}
