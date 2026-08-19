package main

// This file holds the pure state-classification logic, kept free of tsnet and
// network dependencies so it is directly unit testable (see state_test.go).

// NodeState mirrors RWK.Shared.Net.TailscaleState on the C# side. The strings
// are part of the IPC contract and must not be renamed casually.
type NodeState string

const (
	StateDisconnected NodeState = "Disconnected"
	StateConnecting   NodeState = "Connecting"
	StateConnected    NodeState = "Connected"
	StateFault        NodeState = "Fault"
)

// PathType mirrors RWK.Shared.Net.PathType (requirement 5.3).
type PathType string

const (
	PathNone   PathType = "None"
	PathDirect PathType = "Direct"
	PathDerp   PathType = "Derp"
)

// pathFromPeerStatus classifies the path using the peer's netmap status.
// This matches upstream Tailscale's own interpretation: a non-empty CurAddr
// means a direct peer-to-peer path is in use, otherwise a non-empty Relay
// names the DERP region carrying the traffic (requirements 5.3, 5.5).
func pathFromPeerStatus(curAddr, relay string) (PathType, string) {
	if curAddr != "" {
		return PathDirect, ""
	}
	if relay != "" {
		return PathDerp, relay
	}
	return PathNone, ""
}

// pathFromPing classifies the path using a disco ping result, which reflects
// the path actually exercised by the probe rather than the netmap's view.
// Endpoint is set only when direct UDP was used; DERPRegionID is non-zero only
// when the probe was relayed.
func pathFromPing(endpoint string, derpRegionID int, derpCode string) (PathType, string) {
	if endpoint != "" {
		return PathDirect, ""
	}
	if derpRegionID != 0 {
		region := derpCode
		if region == "" {
			// Fall back to the numeric id so the UI always has something to
			// show for requirement 5.5.
			region = itoa(derpRegionID)
		}
		return PathDerp, region
	}
	return PathNone, ""
}

// stateInputs is the full set of observations that determine the reported state.
type stateInputs struct {
	// Started is true once /v1/start has been accepted.
	Started bool
	// Stopping is true while a stop is in flight.
	Stopping bool
	// BackendState is the Tailscale backend state string ("Running",
	// "Starting", "NeedsLogin", "Stopped", "NoState").
	BackendState string
	// LastError is non-empty when start or the tailnet backend failed.
	LastError string
	// EverRunning records whether the backend reached Running at least once,
	// which distinguishes "still coming up" from "path lost".
	EverRunning bool
	// PeerConfigured is true once a peer has been set for edge traffic.
	PeerConfigured bool
	// ConsecutiveProbeFailures counts back-to-back failed peer probes.
	ConsecutiveProbeFailures int
	// FaultAfter is the probe-failure threshold for declaring Fault; 0 disables.
	FaultAfter int
}

// deriveState maps observations onto the four states the C# side understands.
//
// Requirement 5.8: loss of the network path must surface as Fault. Two signals
// produce it — the backend leaving Running after having reached it, and repeated
// peer probe failures while a peer is configured.
func deriveState(in stateInputs) NodeState {
	if in.LastError != "" {
		return StateFault
	}
	if !in.Started || in.Stopping {
		return StateDisconnected
	}

	switch in.BackendState {
	case "Running":
		if in.PeerConfigured && in.FaultAfter > 0 && in.ConsecutiveProbeFailures >= in.FaultAfter {
			return StateFault
		}
		return StateConnected
	case "Stopped":
		if in.EverRunning {
			return StateFault
		}
		return StateDisconnected
	default:
		// "", "NoState", "NeedsLogin", "Starting" and any future value.
		if in.EverRunning {
			return StateFault
		}
		return StateConnecting
	}
}

// itoa avoids pulling strconv into this file's small surface.
func itoa(v int) string {
	if v == 0 {
		return "0"
	}
	neg := v < 0
	if neg {
		v = -v
	}
	var buf [20]byte
	i := len(buf)
	for v > 0 {
		i--
		buf[i] = byte('0' + v%10)
		v /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}
