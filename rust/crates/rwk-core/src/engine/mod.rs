//! Hardware and OS integration.
//!
//! * [`serial`] — serial port enumeration and DTR/RTS key/PTT line control.
//! * [`keying`] — Morse element timing, scheduling and the paddle decider.
//! * [`keying_arbiter`] — shares the one radio output between remote replay and a local logger.
//! * [`edge_sender`] — Client-side orchestrator: edge events → redundant `RWK-PADDLE` frames.
//! * [`edge_station`] — Station-side bridge: tunnel edge datagrams → the replay driver.
//! * [`audio`] — the keyed-sine sidetone generator and its cpal output stream.
//! * [`network`] — native UDP edge transport and port forwarding (no sidecar).
//! * [`tunnel`] — native WireGuard transport, replacing the sidecar's `tsnet` encryption.
//! * [`netstack`] — userspace IP stack over the tunnel, turning inner packets into sockets.
//! * [`netstack_driver`] — the async seam: one task owns the stack, many tasks hold sockets.
//! * [`tunnel_forward`] — port forwarders whose far leg is a tunnel socket (ride WireGuard).
//! * [`relay`] — a minimal packet relay (NAT-traversal fallback) keyed by public key.
//! * [`session`] — a running session: netstack + forwarders wired over a connected tunnel.
//! * [`replay`] — the Station edge replayer: jitter buffer, fail-safes, watchdogs.
//! * [`bus`] — the broadcast event bus that feeds the UI.

pub mod audio;
pub mod bus;
pub mod edge_sender;
pub mod edge_station;
pub mod keying;
pub mod keying_arbiter;
pub mod netstack;
pub mod netstack_driver;
pub mod network;
pub mod relay;
pub mod replay;
pub mod serial;
pub mod session;
pub mod tunnel;
pub mod tunnel_forward;

pub use bus::{CoreEvent, EventBus, EventReceiver};
pub use netstack::{NetStack, TcpHandle, TunnelDevice, UdpHandle};
pub use netstack_driver::{
    NetDriver, NetStackHandle, TcpConn, TcpReceiver, TcpSender, UdpConn, UdpConnReceiver,
    UdpConnSender,
};
pub use network::PathHealth;
pub use relay::{RelayClient, RelayLimits, RelayMetrics, RelayServer};
pub use session::Session;
pub use tunnel_forward::{
    StationTcpResponder, StationUdpResponder, TunnelTcpForward, TunnelUdpForward,
};
pub use tunnel::{connect, Connected, Path, StaticIdentity, TunnelPacket, TunnelPeer, WireGuardTunnel};
pub use edge_sender::EdgeSender;
pub use edge_station::{pump_edges, tunnel_replayer};
pub use keying_arbiter::{
    KeySource, KeyingArbiter, LineObserver, LineUpdate, LocalKeyHandle, RemoteKeyingOutput,
};
pub use keying::{
    EdgeEvent, EdgeSchedule, EdgeScheduleBuilder, ElementKeyer, KeyerElement, KeyerElementTiming,
    PaddleElementEngine, PaddleState,
};
pub use serial::{enumerate_ports, KeyingOutputConfig, PttTiming, SerialKeyingOutput};
