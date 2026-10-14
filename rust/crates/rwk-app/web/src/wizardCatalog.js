// Port-forward wizard preset catalog.
//
// Ported from the C# RWK wizard (src/RWK.Client/Wizard/radios.json). Each preset expands to
// one or more forward rules the operator can create in a couple of clicks instead of looking
// up ports by hand. A preset's `forwards` become ForwardRuleConfigs with the chosen Station
// target address; FlexRadio is intentionally NOT here — it is fully automatic via the
// "Enable FlexRadio discovery" toggle on the Ham Router tab.
//
// Fields:
//   id            stable key
//   vendor        grouping label
//   name          display name shown in the picker
//   targetLabel   what the "Station target address" field means for this preset
//   forwards[]    { proto: "tcp"|"udp", port, label }

export const WIZARD_CATALOG = [
  // ─── Icom ───────────────────────────────────────────────────────────────
  {
    id: "icom.rsba1.lan",
    vendor: "Icom",
    name: "RS-BA1 v2 — direct to radio LAN port",
    targetLabel: "Radio's IP on the Station LAN (MENU → SET → Network → IP Address)",
    forwards: [
      { proto: "udp", port: 50001, label: "RSBA1-Control" },
      { proto: "udp", port: 50002, label: "RSBA1-Serial" },
      { proto: "udp", port: 50003, label: "RSBA1-Audio" },
    ],
  },
  {
    id: "icom.rsba1.pc",
    vendor: "Icom",
    name: "RS-BA1 v2 — via base-station PC",
    targetLabel: "RS-BA1 Server PC address (127.0.0.1 if on the Station PC)",
    forwards: [
      { proto: "udp", port: 50001, label: "RSBA1-Control" },
      { proto: "udp", port: 50002, label: "RSBA1-Serial" },
      { proto: "udp", port: 50003, label: "RSBA1-Audio" },
    ],
  },
  {
    id: "icom.wfview",
    vendor: "Icom",
    name: "wfview / Win4Icom — native LAN protocol",
    targetLabel: "Radio's IP on the Station LAN",
    forwards: [
      { proto: "udp", port: 50001, label: "Icom-Control" },
      { proto: "udp", port: 50002, label: "Icom-Serial" },
      { proto: "udp", port: 50003, label: "Icom-Audio" },
    ],
  },
  // ─── Kenwood ──────────────────────────────────────────────────────────────
  {
    id: "kenwood.kns",
    vendor: "Kenwood",
    name: "KNS direct to radio (ARCP-890, TS-890S)",
    targetLabel: "TS-890S IP on the Station LAN (LAN menu → IP Address)",
    forwards: [
      { proto: "tcp", port: 60000, label: "KNS-Control" },
      { proto: "udp", port: 60001, label: "KNS-Audio" },
    ],
  },
  {
    id: "kenwood.arhp",
    vendor: "Kenwood",
    name: "ARHP host program (conventional remote)",
    targetLabel: "ARHP host PC address (127.0.0.1 if on the Station PC)",
    forwards: [
      { proto: "tcp", port: 50000, label: "ARHP-Control" },
      { proto: "udp", port: 33550, label: "ARVP-Audio" },
    ],
  },
  // ─── Yaesu ─────────────────────────────────────────────────────────────────
  {
    id: "yaesu.sculan10",
    vendor: "Yaesu",
    name: "SCU-LAN10 interface",
    targetLabel: "SCU-LAN10 IP on the Station LAN (SCU-LAN10 Setting Tool)",
    forwards: [
      { proto: "udp", port: 50000, label: "SCU-Control" },
      { proto: "udp", port: 50001, label: "SCU-CAT" },
      { proto: "udp", port: 50002, label: "SCU-Audio-TX" },
      { proto: "udp", port: 50003, label: "SCU-Audio-RX" },
    ],
  },
  // ─── Elecraft ────────────────────────────────────────────────────────────────
  {
    id: "elecraft.k4",
    vendor: "Elecraft",
    name: "K4 / K4D Remote (peer-to-peer)",
    targetLabel: "K4 IP on the Station LAN (CONFIG → NETWORK)",
    forwards: [{ proto: "tcp", port: 9205, label: "K4-Remote" }],
  },
  {
    id: "elecraft.kpa1500",
    vendor: "Elecraft",
    name: "KPA1500 amplifier (Ethernet remote)",
    targetLabel: "KPA1500 IP on the Station LAN (UTIL → ENET → IP ADDR)",
    forwards: [{ proto: "tcp", port: 4626, label: "KPA1500-Remote" }],
  },
  // ─── RemoteRig ─────────────────────────────────────────────────────────────
  {
    id: "remoterig.rrc1258",
    vendor: "RemoteRig",
    name: "RRC-1258 MkII",
    targetLabel: "Radio-RRC IP on the Station LAN (web UI, default 192.168.1.3)",
    forwards: [
      { proto: "udp", port: 13000, label: "RRC-SIP" },
      { proto: "udp", port: 13001, label: "RRC-RTP" },
      { proto: "udp", port: 13002, label: "RRC-CMD" },
    ],
  },
  // ─── 4O3A ─────────────────────────────────────────────────────────────────
  {
    id: "4o3a.antenna-genius",
    vendor: "4O3A",
    name: "Antenna Genius (TCP/IP antenna switch)",
    targetLabel: "Antenna Genius IP on the Station LAN",
    forwards: [{ proto: "tcp", port: 9007, label: "AG-Control" }],
  },
  {
    id: "4o3a.rotator-genius",
    vendor: "4O3A",
    name: "Rotator Genius (TCP/IP rotator controller)",
    targetLabel: "Rotator Genius IP on the Station LAN",
    forwards: [{ proto: "tcp", port: 9006, label: "RG-Control" }],
  },
  // ─── Services / software ────────────────────────────────────────────────────
  {
    id: "svc.rigctld",
    vendor: "Services",
    name: "rigctld (Hamlib CAT server)",
    targetLabel: "rigctld host (127.0.0.1 if on the Station PC)",
    forwards: [{ proto: "tcp", port: 4532, label: "rigctld" }],
  },
  {
    id: "svc.rotctld",
    vendor: "Services",
    name: "rotctld (Hamlib rotator server)",
    targetLabel: "rotctld host (127.0.0.1 if on the Station PC)",
    forwards: [{ proto: "tcp", port: 4533, label: "rotctld" }],
  },
  {
    id: "svc.rdp",
    vendor: "Services",
    name: "Remote Desktop (RDP)",
    targetLabel: "RDP target (127.0.0.1 for the Station PC itself)",
    forwards: [{ proto: "tcp", port: 3389, label: "RDP" }],
  },
  {
    id: "svc.vnc",
    vendor: "Services",
    name: "VNC remote desktop",
    targetLabel: "VNC server address (127.0.0.1 if on the Station PC)",
    forwards: [{ proto: "tcp", port: 5900, label: "VNC" }],
  },
  // ─── Generic ────────────────────────────────────────────────────────────────
  {
    id: "generic.tcp",
    vendor: "Generic",
    name: "Generic single TCP port",
    targetLabel: "Target IP on the Station LAN (or 127.0.0.1 for the Station PC)",
    forwards: [{ proto: "tcp", port: 8000, label: "TCP-Forward" }],
  },
  {
    id: "generic.udp",
    vendor: "Generic",
    name: "Generic single UDP port",
    targetLabel: "Target IP on the Station LAN (or 127.0.0.1 for the Station PC)",
    forwards: [{ proto: "udp", port: 8000, label: "UDP-Forward" }],
  },
];
