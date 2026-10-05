//! Central event bus.
//!
//! Establishes the `tokio::sync::broadcast` channel that carries state updates from
//! the hardware engine to the UI. This replaces the sidecar status-polling loop:
//! instead of the front end polling `GET /v1/status` every 2s, the engine pushes
//! [`CoreEvent`]s and the Tauri layer forwards them to the webview with
//! `app_handle.emit()`.

use tokio::sync::broadcast;

use crate::primitives::TailscaleState;

/// Capacity of the broadcast ring. Slow subscribers lag rather than blocking the
/// engine; a lagging UI resynchronises from the next full-state snapshot.
const EVENT_CHANNEL_CAPACITY: usize = 256;

/// A state update published by the core engine.
#[derive(Debug, Clone, PartialEq)]
pub enum CoreEvent {
    /// A key-down or key-up transition was applied to the output.
    Keying {
        /// Which output received the transition.
        output: KeyingOutputKind,
        /// True for key-down, false for key-up.
        key_down: bool,
        /// Monotonic nanoseconds since engine start when the transition occurred.
        at_ns: u64,
    },

    /// A serial port was opened and is ready for keying.
    SerialConnected {
        /// Port name (e.g. `COM3`).
        port: String,
    },

    /// A serial port was closed or lost.
    SerialDisconnected {
        /// Port name.
        port: String,
        /// Human-readable reason.
        reason: String,
    },

    /// The paddle contacts changed state.
    PaddleState {
        /// Dit paddle is closed.
        dit: bool,
        /// Dah paddle is closed.
        dah: bool,
    },

    /// Mesh link state changed.
    ConnectionState(TailscaleState),

    /// Edge transport counters, for the UI's packet-statistics panel.
    EdgeStats {
        /// Datagrams transmitted.
        tx_datagrams: u64,
        /// Datagrams received.
        rx_datagrams: u64,
        /// Datagrams dropped because no peer was configured.
        drop_no_peer: u64,
        /// Datagrams dropped because of a foreign source.
        drop_foreign: u64,
    },

    /// A fail-safe condition fired; the key output was forced up before this event.
    FailSafe {
        /// The F-number of the condition (1-10).
        code: u8,
        /// Operator-facing description.
        message: String,
    },

    /// A configuration change was applied (so the UI can echo it back).
    ConfigChanged {
        /// Dot-path of the changed setting, e.g. `keyer.speed_wpm`.
        key: String,
        /// New value rendered for display.
        value: String,
    },
}

/// Which output a keying transition was applied to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyingOutputKind {
    /// The serial key line (DTR/RTS).
    Serial,
    /// The local sidetone oscillator.
    Sidetone,
    /// The PTT line.
    Ptt,
}

/// Receiving end of the event bus.
pub type EventReceiver = broadcast::Receiver<CoreEvent>;

/// Publishing end of the event bus.
///
/// Cloneable and cheap to move across threads/tasks; publishing never blocks.
#[derive(Debug, Clone)]
pub struct EventBus {
    tx: broadcast::Sender<CoreEvent>,
}

impl Default for EventBus {
    fn default() -> Self {
        Self::new()
    }
}

impl EventBus {
    /// Creates an event bus with the default channel capacity.
    #[must_use]
    pub fn new() -> Self {
        let (tx, _rx) = broadcast::channel(EVENT_CHANNEL_CAPACITY);
        Self { tx }
    }

    /// Subscribes to the bus. Every subscriber receives every event published after
    /// it subscribes.
    #[must_use]
    pub fn subscribe(&self) -> EventReceiver {
        self.tx.subscribe()
    }

    /// Publishes an event. Returns the number of live subscribers that received it.
    ///
    /// Never blocks: a full ring drops the oldest event for lagging subscribers.
    pub fn publish(&self, event: CoreEvent) -> usize {
        self.tx.send(event).unwrap_or(0)
    }

    /// Number of live subscribers.
    #[must_use]
    pub fn subscriber_count(&self) -> usize {
        self.tx.receiver_count()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn published_events_reach_subscribers() {
        let bus = EventBus::new();
        let mut rx = bus.subscribe();
        assert_eq!(bus.subscriber_count(), 1);

        bus.publish(CoreEvent::SerialConnected { port: "COM3".into() });
        let received = rx.recv().await.expect("event");
        assert_eq!(received, CoreEvent::SerialConnected { port: "COM3".into() });
    }

    #[tokio::test]
    async fn all_subscribers_receive_each_event() {
        let bus = EventBus::new();
        let mut a = bus.subscribe();
        let mut b = bus.subscribe();
        let sent = bus.publish(CoreEvent::ConnectionState(TailscaleState::Connected));
        assert_eq!(sent, 2);
        assert_eq!(a.recv().await.unwrap(), CoreEvent::ConnectionState(TailscaleState::Connected));
        assert_eq!(b.recv().await.unwrap(), CoreEvent::ConnectionState(TailscaleState::Connected));
    }

    #[test]
    fn publish_with_no_subscribers_is_not_an_error() {
        let bus = EventBus::new();
        assert_eq!(bus.publish(CoreEvent::PaddleState { dit: true, dah: false }), 0);
    }
}
