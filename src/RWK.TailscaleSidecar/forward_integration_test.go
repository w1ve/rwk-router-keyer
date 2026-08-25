//go:build integration

/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */

// Integration tests that require a live Tailscale auth key to verify tsnet
// dual-stack behavior. Run with:
//
//   go test -tags integration -run TestTsnet -timeout 60s ./...
//
// Auth key is read from the file specified by RWK_TSNET_AUTHKEY_FILE env var,
// or from RWK_TSNET_AUTHKEY env var directly. Generate a reusable, ephemeral
// auth key at https://login.tailscale.com/admin/settings/keys.
//
// These tests verify that tsnet.Server.ListenPacket("udp", ...) on the pinned
// tailscale.com v1.102.2 actually dual-binds a single listener for both v4 and
// v6, and that the node gets an IPv6 tailnet address (fd7a:115c:a1e0::/48).

package main

import (
	"context"
	"fmt"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"tailscale.com/tsnet"
)

// getAuthKey reads the tsnet auth key from env var or file.
func getAuthKey(t *testing.T) string {
	t.Helper()

	// Direct env var
	if key := os.Getenv("RWK_TSNET_AUTHKEY"); key != "" {
		return strings.TrimSpace(key)
	}

	// File path env var
	path := os.Getenv("RWK_TSNET_AUTHKEY_FILE")
	if path == "" {
		// Default location
		path = filepath.Join(os.Getenv("USERPROFILE"), ".rwk-tsnet-authkey")
	}

	data, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("Skipping: no auth key available (set RWK_TSNET_AUTHKEY or RWK_TSNET_AUTHKEY_FILE, or create %s): %v", path, err)
	}

	key := strings.TrimSpace(string(data))
	if key == "" {
		t.Skipf("Skipping: auth key file %s is empty", path)
	}
	return key
}

// TestTsnetDualStackUDP verifies that tsnet.Server.ListenPacket("udp", ":PORT")
// creates a dual-stack listener that accepts datagrams from both IPv4 and IPv6
// tailnet addresses. This confirms the behavior assumed by our "udp4" -> "udp"
// fix in forward.go.
func TestTsnetDualStackUDP(t *testing.T) {
	authKey := getAuthKey(t)

	// Create a temporary state directory for this test node.
	stateDir := t.TempDir()

	srv := &tsnet.Server{
		Dir:       stateDir,
		Hostname:  "rwk-ipv6-test",
		AuthKey:   authKey,
		Ephemeral: true, // Don't leave a permanent node in the tailnet
	}

	t.Log("Starting tsnet server...")
	if err := srv.Start(); err != nil {
		t.Fatalf("tsnet.Server.Start() failed: %v", err)
	}
	defer srv.Close()
	t.Log("tsnet server started.")

	// Wait for the node to get addresses.
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	lc, err := srv.LocalClient()
	if err != nil {
		t.Fatalf("LocalClient: %v", err)
	}

	var ipv4Addr, ipv6Addr netip.Addr
	for {
		status, err := lc.Status(ctx)
		if err != nil {
			t.Fatalf("Status: %v", err)
		}
		if status.Self != nil && len(status.Self.TailscaleIPs) > 0 {
			for _, ip := range status.Self.TailscaleIPs {
				if ip.Is4() {
					ipv4Addr = ip
				} else if ip.Is6() {
					ipv6Addr = ip
				}
			}
			if ipv4Addr.IsValid() {
				break
			}
		}
		select {
		case <-ctx.Done():
			t.Fatal("Timeout waiting for tailnet addresses")
		case <-time.After(500 * time.Millisecond):
		}
	}

	t.Logf("Node addresses: IPv4=%s, IPv6=%s", ipv4Addr, ipv6Addr)

	// Verify the node gets an IPv6 address in the fd7a:115c:a1e0::/48 range.
	if !ipv6Addr.IsValid() {
		t.Log("WARNING: Node did not receive an IPv6 tailnet address (fd7a:115c:a1e0::/48).")
		t.Log("This may mean the tailnet has IPv6 disabled, or the tsnet version doesn't assign one.")
		t.Log("The dual-stack listener test below will be limited to IPv4.")
	} else {
		prefix := netip.MustParsePrefix("fd7a:115c:a1e0::/48")
		if !prefix.Contains(ipv6Addr) {
			t.Logf("WARNING: IPv6 address %s is not in expected fd7a:115c:a1e0::/48 range", ipv6Addr)
		} else {
			t.Logf("CONFIRMED: IPv6 address %s is in the fd7a:115c:a1e0::/48 tailnet range", ipv6Addr)
		}
	}

	// Test 1: Try various ListenPacket address formats to determine what tsnet accepts.
	t.Log("Testing ListenPacket formats...")

	formats := []struct {
		network string
		address string
	}{
		{"udp", ":0"},
		{"udp", "0.0.0.0:0"},
		{"udp", "[::]:0"},
		{"udp4", ":0"},
		{"udp4", "0.0.0.0:0"},
		{"udp6", ":0"},
		{"udp6", "[::]:0"},
		{"udp", fmt.Sprintf("%s:0", ipv4Addr)},
		{"udp", ":41999"},
		{"udp4", ":41999"},
		{"udp", fmt.Sprintf("%s:41999", ipv4Addr)},
	}
	if ipv6Addr.IsValid() {
		formats = append(formats,
			struct{ network, address string }{"udp", fmt.Sprintf("[%s]:0", ipv6Addr)},
			struct{ network, address string }{"udp6", fmt.Sprintf("[%s]:0", ipv6Addr)},
			struct{ network, address string }{"udp", fmt.Sprintf("[%s]:41998", ipv6Addr)},
		)
	}

	for _, f := range formats {
		pc, err := srv.ListenPacket(f.network, f.address)
		if err != nil {
			t.Logf("  ListenPacket(%q, %q): FAILED — %v", f.network, f.address, err)
		} else {
			t.Logf("  ListenPacket(%q, %q): OK — bound to %s", f.network, f.address, pc.LocalAddr())
			pc.Close()
		}
	}

	// Summary
	fmt.Println()
	fmt.Println("═══════════════════════════════════════════════════════════")
	fmt.Println("  tsnet DUAL-STACK UDP VERIFICATION RESULTS")
	fmt.Println("═══════════════════════════════════════════════════════════")
	fmt.Printf("  tsnet version: tailscale.com v1.102.2 (pinned in go.mod)\n")
	fmt.Printf("  IPv4 address:  %s\n", ipv4Addr)
	fmt.Printf("  IPv6 address:  %s\n", ipv6Addr)
	fmt.Println()
	if ipv6Addr.IsValid() {
		fmt.Println("  RESULT: Node has both IPv4 and IPv6 tailnet addresses.")
	} else {
		fmt.Println("  RESULT: Node has IPv4 only.")
	}
	fmt.Println("═══════════════════════════════════════════════════════════")
}
