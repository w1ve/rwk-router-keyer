// Thin wrapper over the Station executable's Tauri command bridge. Every function maps to a
// #[tauri::command] in src/lib.rs (the `station_*` set). The Station binary and the Client
// binary share this web bundle but register different command sets; the UI chosen at startup
// (see main.jsx) only ever calls the commands its own binary exposes.
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

export const stationApi = {
  ping: () => invoke("station_ping"),

  // Ports / keying configuration
  serialPorts: () => invoke("station_serial_ports"),
  selectKeyingPort: (port) => invoke("station_select_keying_port", { port }),
  setKeyLine: (line) => invoke("station_set_key_line", { line }),
  setPttLine: (line) => invoke("station_set_ptt_line", { line }),

  // Logger (WinKeyer) input
  setLoggerPort: (port) => invoke("station_set_logger_port", { port }),
  setLoggerEnabled: (enabled) => invoke("station_set_logger_enabled", { enabled }),

  // Pairing — WAN (the Station waits for the client over the relay)
  listen: (relay, code) => invoke("station_listen", { relay, code }),
  disconnect: () => invoke("station_disconnect"),

  // Pairing — transport + LAN (the Station browses advertised keyers and picks one)
  setTransport: (transport) => invoke("station_set_transport", { transport }),
  lanBrowse: () => invoke("station_lan_browse"),
  connectLan: (key, endpoint) => invoke("station_connect_lan", { key, endpoint }),
};

// Subscribes to the controller event stream. `handler` receives each StationEvent (a JS
// object with a `kind` discriminator). Returns a promise of an unlisten function. Shares the
// `rwk://event` channel name with the Client, so the event plumbing is identical.
export function onRwkEvent(handler) {
  return listen("rwk://event", (e) => handler(e.payload));
}
