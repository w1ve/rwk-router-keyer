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
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func newTestAPI(t *testing.T) (*API, *Node) {
	t.Helper()
	cfg := Config{
		Hostname:     "rwk-test",
		StateDir:     t.TempDir(),
		PollInterval: 2 * time.Second,
		FaultAfter:   3,
	}
	node := NewNode(cfg, func(string, ...any) {})
	t.Cleanup(node.Close)
	return NewAPI(node, "test-token", func(string, ...any) {}), node
}

func do(t *testing.T, api *API, method, path, token, body string) *httptest.ResponseRecorder {
	t.Helper()
	var r *http.Request
	if body == "" {
		r = httptest.NewRequest(method, path, nil)
	} else {
		r = httptest.NewRequest(method, path, strings.NewReader(body))
	}
	if token != "" {
		r.Header.Set(tokenHeader, token)
	}
	w := httptest.NewRecorder()
	api.srv.Handler.ServeHTTP(w, r)
	return w
}

func TestAPIRequiresToken(t *testing.T) {
	api, _ := newTestAPI(t)

	if got := do(t, api, "GET", "/v1/status", "", "").Code; got != http.StatusUnauthorized {
		t.Fatalf("missing token: got %d, want 401", got)
	}
	if got := do(t, api, "GET", "/v1/status", "wrong-token", "").Code; got != http.StatusUnauthorized {
		t.Fatalf("wrong token: got %d, want 401", got)
	}
	if got := do(t, api, "GET", "/v1/status", "test-token", "").Code; got != http.StatusOK {
		t.Fatalf("correct token: got %d, want 200", got)
	}
}

// The status document is the contract the C# ITailscaleNode implementation
// polls, so its shape is asserted directly.
func TestStatusDocumentContract(t *testing.T) {
	api, _ := newTestAPI(t)

	w := do(t, api, "GET", "/v1/status", "test-token", "")
	var doc StatusDocument
	if err := json.Unmarshal(w.Body.Bytes(), &doc); err != nil {
		t.Fatalf("decode: %v (body %s)", err, w.Body.String())
	}

	if doc.Protocol != ipcProtocolVersion {
		t.Errorf("protocol = %d, want %d", doc.Protocol, ipcProtocolVersion)
	}
	// Requirement 5.1: userspace only, asserted rather than assumed.
	if !doc.Userspace {
		t.Error("userspace = false, want true")
	}
	if doc.State != StateDisconnected {
		t.Errorf("state = %s, want %s before start", doc.State, StateDisconnected)
	}
	if doc.Path != PathNone {
		t.Errorf("path = %s, want %s before start", doc.Path, PathNone)
	}
	if doc.RoundTripMs != -1 {
		t.Errorf("roundTripMs = %v, want -1 when unmeasured", doc.RoundTripMs)
	}
	if doc.DerpRegion != "" {
		t.Errorf("derpRegion = %q, want empty when not relayed", doc.DerpRegion)
	}
	// The edge transport declaration drives the Station's jitter profile choice.
	if doc.Edge.Transport != "udp" {
		t.Errorf("edge.transport = %q, want udp", doc.Edge.Transport)
	}
	if doc.Edge.JitterProfile != jitterProfilePathAdaptive {
		t.Errorf("edge.jitterProfile = %q, want %q", doc.Edge.JitterProfile, jitterProfilePathAdaptive)
	}
}

func TestPeerRequiresAddress(t *testing.T) {
	api, _ := newTestAPI(t)

	if got := do(t, api, "POST", "/v1/peer", "test-token", `{"address":"","edgePort":41000}`).Code; got != http.StatusBadRequest {
		t.Fatalf("empty address: got %d, want 400", got)
	}
	if got := do(t, api, "POST", "/v1/peer", "test-token", `{"address":"100.64.0.9","edgePort":41000}`).Code; got != http.StatusOK {
		t.Fatalf("valid peer: got %d, want 200", got)
	}
}

func TestEdgeCallbackRejectsNonLoopback(t *testing.T) {
	api, _ := newTestAPI(t)

	if got := do(t, api, "POST", "/v1/edge/callback", "test-token", `{"address":"192.0.2.10:5000"}`).Code; got != http.StatusBadRequest {
		t.Fatalf("non-loopback callback: got %d, want 400", got)
	}
	if got := do(t, api, "POST", "/v1/edge/callback", "test-token", `{"address":"127.0.0.1:5000"}`).Code; got != http.StatusOK {
		t.Fatalf("loopback callback: got %d, want 200", got)
	}
}

func TestForwardRejectsUnknownKindAndMissingPort(t *testing.T) {
	api, _ := newTestAPI(t)

	if got := do(t, api, "POST", "/v1/forwards", "test-token", `{"kind":"sideways","tailnetPort":9000}`).Code; got != http.StatusBadRequest {
		t.Fatalf("unknown kind: got %d, want 400", got)
	}
	if got := do(t, api, "POST", "/v1/forwards", "test-token", `{"kind":"in","tailnetPort":9000}`).Code; got != http.StatusBadRequest {
		t.Fatalf("inbound without localPort: got %d, want 400", got)
	}
	// An outbound forward with no peer configured cannot resolve a target.
	if got := do(t, api, "POST", "/v1/forwards", "test-token", `{"kind":"out","tailnetPort":9000}`).Code; got != http.StatusBadRequest {
		t.Fatalf("outbound without peer: got %d, want 400", got)
	}
}

func TestIdleForTracksRequests(t *testing.T) {
	api, _ := newTestAPI(t)

	// An unauthorized request must not count as liveness.
	do(t, api, "GET", "/v1/status", "wrong-token", "")
	before := api.IdleFor()
	time.Sleep(20 * time.Millisecond)
	if api.IdleFor() <= before {
		t.Fatal("idle time did not advance after an unauthorized request")
	}

	do(t, api, "GET", "/v1/status", "test-token", "")
	if api.IdleFor() > 5*time.Second {
		t.Fatalf("idle time = %v after authorized request, want near zero", api.IdleFor())
	}
}
