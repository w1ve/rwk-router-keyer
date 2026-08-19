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
	"crypto/subtle"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"sync/atomic"
	"time"
)

// tokenHeader carries the shared secret. The API is bound to loopback, but any
// local process can reach a loopback port, so a token is still required: an
// unauthenticated local endpoint here would let any process on the machine join
// or leave the tailnet and open tunnels into the Station's network.
const tokenHeader = "X-RWK-Token" //nolint:gosec // header name, not a credential

// API is the loopback HTTP IPC surface.
//
// Localhost TCP is used rather than a named pipe so this program stays portable
// and so the .NET side can use an ordinary HttpClient.
type API struct {
	node  *Node
	token string
	logf  func(string, ...any)
	srv   *http.Server

	lastRequestUnixNano atomic.Int64
}

func NewAPI(node *Node, token string, logf func(string, ...any)) *API {
	a := &API{node: node, token: token, logf: logf}
	a.touch()

	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/health", a.handleHealth)
	mux.HandleFunc("GET /v1/status", a.handleStatus)
	mux.HandleFunc("POST /v1/start", a.handleStart)
	mux.HandleFunc("POST /v1/stop", a.handleStop)
	mux.HandleFunc("POST /v1/peer", a.handlePeer)
	mux.HandleFunc("POST /v1/edge/callback", a.handleEdgeCallback)
	mux.HandleFunc("GET /v1/forwards", a.handleForwardsList)
	mux.HandleFunc("POST /v1/forwards", a.handleForwardsAdd)
	mux.HandleFunc("DELETE /v1/forwards/{id}", a.handleForwardsDelete)

	a.srv = &http.Server{
		Handler:           a.authenticate(mux),
		ReadHeaderTimeout: 5 * time.Second,
	}
	return a
}

func (a *API) Serve(ln net.Listener) error {
	err := a.srv.Serve(ln)
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

func (a *API) Shutdown(ctx context.Context) error { return a.srv.Shutdown(ctx) }

func (a *API) touch() { a.lastRequestUnixNano.Store(time.Now().UnixNano()) }

// IdleFor reports how long it has been since the last authenticated request.
// The supervisor's 2s status polling doubles as a liveness signal.
func (a *API) IdleFor() time.Duration {
	return time.Since(time.Unix(0, a.lastRequestUnixNano.Load()))
}

func (a *API) authenticate(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got := r.Header.Get(tokenHeader)
		if subtle.ConstantTimeCompare([]byte(got), []byte(a.token)) != 1 {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		a.touch()
		next.ServeHTTP(w, r)
	})
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, code int, err error) {
	writeJSON(w, code, map[string]string{"error": err.Error()})
}

func (a *API) handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"protocol":      ipcProtocolVersion,
		"userspace":     true,
		"edgeTransport": EdgeTransport,
	})
}

func (a *API) handleStatus(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, a.node.Status())
}

type startRequest struct {
	// AuthKey is the Tailscale pre-auth key (requirement 5.2). It is passed in
	// a request body rather than on the command line so it never appears in the
	// process list.
	AuthKey string `json:"authKey"`
}

func (a *API) handleStart(w http.ResponseWriter, r *http.Request) {
	var req startRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<10)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	if err := a.node.Start(req.AuthKey); err != nil {
		if errors.Is(err, errAlreadyStarted) {
			writeJSON(w, http.StatusConflict, a.node.Status())
			return
		}
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	// Accepted: the caller polls /v1/status until State is Connected or Fault.
	writeJSON(w, http.StatusAccepted, a.node.Status())
}

func (a *API) handleStop(w http.ResponseWriter, _ *http.Request) {
	a.node.Stop()
	writeJSON(w, http.StatusOK, a.node.Status())
}

type peerRequest struct {
	// Address is a Tailscale IP, MagicDNS name, or hostname.
	Address string `json:"address"`
	// EdgePort is the peer's tailnet UDP port for edge datagrams, learned over
	// the control channel. 0 leaves outbound edges undeliverable.
	EdgePort int `json:"edgePort"`
}

func (a *API) handlePeer(w http.ResponseWriter, r *http.Request) {
	var req peerRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<10)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	if err := a.node.SetPeer(req.Address, req.EdgePort); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	writeJSON(w, http.StatusOK, a.node.Status())
}

type edgeCallbackRequest struct {
	// Address is the loopback UDP endpoint the .NET app listens on, e.g.
	// "127.0.0.1:51500".
	Address string `json:"address"`
}

func (a *API) handleEdgeCallback(w http.ResponseWriter, r *http.Request) {
	var req edgeCallbackRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<10)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	if err := a.node.SetEdgeCallback(req.Address); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	writeJSON(w, http.StatusOK, a.node.Status().Edge)
}

func (a *API) handleForwardsList(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, a.node.fwd.List())
}

func (a *API) handleForwardsAdd(w http.ResponseWriter, r *http.Request) {
	var spec forwardSpec
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<10)).Decode(&spec); err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	info, err := a.node.fwd.Add(spec)
	if err != nil {
		writeErr(w, http.StatusBadRequest, err)
		return
	}
	writeJSON(w, http.StatusCreated, info)
}

func (a *API) handleForwardsDelete(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if !a.node.fwd.Remove(id) {
		writeErr(w, http.StatusNotFound, errors.New("no such forward: "+id))
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
