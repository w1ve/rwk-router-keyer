// Thin wrapper over the Tauri command bridge. Every function maps to a #[tauri::command]
// in src/lib.rs; keeping them in one place means the UI never scatters raw invoke() calls.
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

export const api = {
  ping: () => invoke("ping"),

  // Keyer settings
  setSpeed: (wpm) => invoke("set_speed", { wpm }),
  setWeight: (weight) => invoke("set_weight", { weight }),
  setMode: (mode) => invoke("set_mode", { mode }),
  setPaddleReverse: (reverse) => invoke("set_paddle_reverse", { reverse }),

  // Sidetone
  setToneFrequency: (hz) => invoke("set_tone_frequency", { hz }),
  setToneVolume: (percent) => invoke("set_tone_volume", { percent }),
  audioDevices: () => invoke("audio_devices"),

  // Ports
  serialPorts: () => invoke("serial_ports"),
  selectKeyingPort: (port) => invoke("select_keying_port", { port }),
  selectPaddlePort: (port) => invoke("select_paddle_port", { port }),
  selectWinkeyerPort: (port) => invoke("select_winkeyer_port", { port }),

  // Keying
  sendCw: (text) => invoke("send_cw", { text }),
  testTx: () => invoke("test_tx"),

  // Pairing — WAN (relay + code)
  connect: (relay, code) => invoke("connect", { relay, code }),
  disconnect: () => invoke("disconnect"),

  // Pairing — transport + LAN (this keyer advertises itself; the Station picks it)
  setTransport: (transport) => invoke("set_transport", { transport }),
  setCallsign: (callsign) => invoke("set_callsign", { callsign }),
  setKeyerId: (keyerId) => invoke("set_keyer_id", { keyerId }),
  lanAdvertise: (on) => invoke("lan_advertise", { on }),

  // Ham Router (port forwarding)
  forwardRulesRefresh: () => invoke("forward_rules_refresh"),
  addForwardRule: (name, protocol, clientPort, stationPort, stationTargetAddress) =>
    invoke("add_forward_rule", {
      name,
      protocol,
      clientPort,
      stationPort,
      stationTargetAddress,
    }),
  removeForwardRule: (id) => invoke("remove_forward_rule", { id }),
  setForwardRuleEnabled: (id, enabled) =>
    invoke("set_forward_rule_enabled", { id, enabled }),
  setFlexDiscovery: (enabled) => invoke("set_flex_discovery", { enabled }),
};

// Subscribes to the single controller event stream. `handler` receives each ClientEvent
// (already a JS object with a `kind` discriminator). Returns an unlisten function.
export function onRwkEvent(handler) {
  return listen("rwk://event", (e) => handler(e.payload));
}
