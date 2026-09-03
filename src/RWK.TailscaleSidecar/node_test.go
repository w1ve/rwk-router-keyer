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
	"testing"
	"time"
)

// TestSetPeerClearsPeer verifies the load-bearing behavior behind the
// Fault-on-abandoned-pair fix: configuring a peer for a pairing ATTEMPT and then
// clearing it (empty spec) must leave PeerConfigured=false, so probe failures on
// a dead peer can no longer drive the node to Fault.
func TestSetPeerClearsPeer(t *testing.T) {
	cfg := Config{
		Hostname:     "rwk-test",
		StateDir:     t.TempDir(),
		PollInterval: 2 * time.Second,
		FaultAfter:   3,
	}
	node := NewNode(cfg, func(string, ...any) {})
	t.Cleanup(node.Close)

	// Configure a literal-address peer as the pairing attempt would.
	if err := node.SetPeer("100.64.0.9", 41373); err != nil {
		t.Fatalf("SetPeer(addr) returned error: %v", err)
	}
	if got := node.Status(); got.PeerSpec == "" {
		t.Fatalf("after SetPeer, PeerSpec should be set, got empty")
	}

	// Simulate a run of failed probes against the (now dead) peer.
	node.mu.Lock()
	node.probeFailures = 9
	node.mu.Unlock()

	// Abandon the attempt: clearing the peer with an empty spec.
	if err := node.SetPeer("", 0); err != nil {
		t.Fatalf("SetPeer(\"\") should clear the peer, got error: %v", err)
	}

	node.mu.Lock()
	spec := node.peerSpec
	failures := node.probeFailures
	node.mu.Unlock()

	if spec != "" {
		t.Fatalf("peerSpec should be cleared, got %q", spec)
	}
	if failures != 0 {
		t.Fatalf("probeFailures should be reset on clear, got %d", failures)
	}
	if node.edge.Peer().IsValid() {
		t.Fatalf("edge relay peer should be cleared, got %v", node.edge.Peer())
	}

	// The decisive check: with the peer cleared, a Running backend with a high
	// probe-failure count must NOT be Fault — it stays Connected, matching the
	// "probe failures ignored with no peer configured" deriveState case.
	node.mu.Lock()
	node.started = true
	node.backend = "Running"
	node.everRunning = true
	node.probeFailures = 9 // as if a stale probe run lingered
	node.mu.Unlock()

	if got := node.Status().State; got != StateConnected {
		t.Fatalf("cleared peer + high probe failures should stay Connected, got %s", got)
	}
}
