package main

import "testing"

func TestPathFromPeerStatus(t *testing.T) {
	cases := []struct {
		name     string
		curAddr  string
		relay    string
		wantPath PathType
		wantDerp string
	}{
		{"direct wins over relay", "203.0.113.7:41641", "sfo", PathDirect, ""},
		{"relayed when no direct addr", "", "sfo", PathDerp, "sfo"},
		{"nothing known", "", "", PathNone, ""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			path, derp := pathFromPeerStatus(tc.curAddr, tc.relay)
			if path != tc.wantPath || derp != tc.wantDerp {
				t.Fatalf("got (%s,%q), want (%s,%q)", path, derp, tc.wantPath, tc.wantDerp)
			}
		})
	}
}

func TestPathFromPing(t *testing.T) {
	if p, d := pathFromPing("203.0.113.7:41641", 0, ""); p != PathDirect || d != "" {
		t.Fatalf("direct: got (%s,%q)", p, d)
	}
	if p, d := pathFromPing("", 9, "nyc"); p != PathDerp || d != "nyc" {
		t.Fatalf("derp: got (%s,%q)", p, d)
	}
	// Requirement 5.5 needs a region identifier even when the code is missing.
	if p, d := pathFromPing("", 12, ""); p != PathDerp || d != "12" {
		t.Fatalf("derp without code: got (%s,%q)", p, d)
	}
	if p, d := pathFromPing("", 0, ""); p != PathNone || d != "" {
		t.Fatalf("none: got (%s,%q)", p, d)
	}
}

func TestDeriveState(t *testing.T) {
	cases := []struct {
		name string
		in   stateInputs
		want NodeState
	}{
		{"fresh process", stateInputs{}, StateDisconnected},
		{"start error is a fault", stateInputs{LastError: "boom"}, StateFault},
		{"coming up", stateInputs{Started: true, BackendState: "Starting"}, StateConnecting},
		{"needs login", stateInputs{Started: true, BackendState: "NeedsLogin"}, StateConnecting},
		{"running", stateInputs{Started: true, BackendState: "Running"}, StateConnected},
		{
			"path lost after running (5.8)",
			stateInputs{Started: true, BackendState: "NoState", EverRunning: true},
			StateFault,
		},
		{
			"backend stopped after running (5.8)",
			stateInputs{Started: true, BackendState: "Stopped", EverRunning: true},
			StateFault,
		},
		{
			"probe failures below threshold stay connected",
			stateInputs{Started: true, BackendState: "Running", PeerConfigured: true, ConsecutiveProbeFailures: 2, FaultAfter: 3},
			StateConnected,
		},
		{
			"probe failures at threshold fault (5.8)",
			stateInputs{Started: true, BackendState: "Running", PeerConfigured: true, ConsecutiveProbeFailures: 3, FaultAfter: 3},
			StateFault,
		},
		{
			"probe failures ignored with no peer configured",
			stateInputs{Started: true, BackendState: "Running", ConsecutiveProbeFailures: 9, FaultAfter: 3},
			StateConnected,
		},
		{
			"stopping reports disconnected",
			stateInputs{Started: true, Stopping: true, BackendState: "Running", EverRunning: true},
			StateDisconnected,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := deriveState(tc.in); got != tc.want {
				t.Fatalf("got %s, want %s", got, tc.want)
			}
		})
	}
}

func TestItoa(t *testing.T) {
	for _, tc := range []struct {
		in   int
		want string
	}{{0, "0"}, {7, "7"}, {41641, "41641"}, {-12, "-12"}} {
		if got := itoa(tc.in); got != tc.want {
			t.Fatalf("itoa(%d) = %q, want %q", tc.in, got, tc.want)
		}
	}
}
