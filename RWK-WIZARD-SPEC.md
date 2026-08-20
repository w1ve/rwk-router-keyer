# RWK Port-Forward Wizard — Design Specification

**Version:** 1.0-draft
**Date:** 2026-08-20
**Author:** W1VE
**Companion to:** RWK Router/Keyer (https://github.com/w1ve/rwk-router-keyer)

---

## 1. Purpose

The RWK Client already supports arbitrary TCP/UDP port forwarding over the Tailscale
mesh. What it does not do is tell an operator *which* ports to forward for their
particular radio and control software. That knowledge is scattered across a dozen
vendor manuals, each with its own terminology, its own defaults, and its own
undocumented quirks.

The Wizard closes that gap. It ships **inside the RWK Client** — not as a companion
application — reachable from the Port Forwards panel and from the File menu. The
operator answers three or four questions, and the Wizard produces three outputs:

1. **Live port forward rules**, written directly into the Port Forwards grid. No
   export/import round trip.
2. **A saved JSON profile** (`[radioname].rwkprofile.json`) — a portable artifact for
   backup, for sharing with another operator, or for reloading on a second Client PC.
3. **A plain-text setup guide** (`[radioname]-readme.txt`), opened automatically in
   the operator's default text editor the moment it is written.

Optionally it also emits virtual serial port configuration files (`.vspe`, com0com
command lines, or a `ser2net.yaml` fragment) for the generic RS-232 tunnelling case.

Being in-process rather than a separate tool buys two things worth designing around:
the Wizard can validate against the Client's *actual* current rule set and socket
state rather than guessing (§8), and it can offer a clean undo (§4.4).

---

## 2. Design Principles

### 2.1 The Station is always the currently paired Station

The Wizard does not ask for a Tailscale IP or a Station identity. RWK already knows
which Station is paired, and port forwards are Client-side rules pushed to that
Station. Asking again would be redundant and would create a second source of truth.

**Consequence:** the Wizard's output JSON contains no Station address field. The
`stationTarget` field in each rule refers to a device *on the Station's LAN* (or
`127.0.0.1` for software running on the Station PC itself) — not to the Station.

### 2.2 Port identity is a first-class property

Several remote-control protocols carry port numbers *inside* their own payloads, or
derive the client-side port from the server-side configuration. For those protocols
the client port and the station port **must be identical**, and the Wizard must
refuse to renumber them.

The canonical example is Icom's RS-BA1. DH1TW documented this precisely: the control
connection lets you specify the port explicitly on the client side, but
<cite index="3-3">the software instead takes the CAT and audio UDP port settings from the
server rather than asking for them on the client, producing a "Virtual serial device
error" when they differ</cite>. SIP-based systems (RemoteRig) have the same
property for a different reason — SDP bodies carry endpoint information.

Every catalog entry therefore carries `portIdentity`:

| Value | Meaning |
|---|---|
| `required` | Client port MUST equal Station port. Wizard refuses to renumber. |
| `floating` | Ports may be remapped freely. |
| `unknown` | Not yet determined; Wizard treats as `required` (safe default). |

### 2.3 The catalog is data, not code

Radio and software definitions live in a versioned `radios.json` shipped alongside
the Wizard and updatable independently of the binary. Operators can submit new
entries as pull requests without a build.

### 2.4 Every input explains itself

An operator who understands *why* a field exists can debug their own station later.
An operator who typed a value because a box demanded one cannot. Every prompt in the
Wizard therefore carries three pieces of explanatory copy, and — critically — that
copy lives in the catalog alongside the ports, not hardcoded in the UI:

| Field | Purpose |
|---|---|
| `why` | What this value does and where it ends up. One or two sentences. |
| `howToFind` | Concretely, where to look. Radio menu path, software dialog, `ipconfig`. Model-specific where possible. |
| `ifWrong` | The symptom of getting it wrong — especially when the failure is silent. |

`ifWrong` is the one that earns its keep. Most port-forwarding failures do not
produce an error; the rule binds, the status column reads "Listening", and the
control software simply times out. Naming that symptom in advance converts an
evening of packet captures into a ten-second correction.

The `why` text is always visible next to the field. `howToFind` and `ifWrong` sit
behind a disclosure control so the step stays scannable for operators who already
know the answer.

### 2.5 Honesty about what is verified

Every catalog entry carries a `confidence` field. Shipping a confident wrong port
number costs an operator an evening of debugging; shipping an honestly-flagged
unverified one costs them nothing.

| Value | Meaning |
|---|---|
| `verified` | Port numbers cited from vendor documentation (`source` URL required). |
| `community` | Multiple consistent field reports, no vendor citation. |
| `unverified` | Best guess. Wizard displays a banner. |

---

## 3. Wizard Flow

```
┌─ Step 1 ── Radio ──────────────────────────────────────────────┐
│  Searchable list, grouped by vendor.                           │
│  "Generic RS-232 device" and "Generic TCP/UDP service"         │
│  always appear at the bottom.                                  │
└────────────────────────────────────────────────────────────────┘
                              ↓
┌─ Step 2 ── Control path ───────────────────────────────────────┐
│  Filtered to paths the selected radio supports. e.g. for an    │
│  IC-7610:  · RS-BA1 v2, radio's own LAN port                   │
│            · RS-BA1 v2, via base-station PC                    │
│            · wfview (native Icom LAN protocol)                 │
│            · Generic CI-V over serial bridge                   │
└────────────────────────────────────────────────────────────────┘
                              ↓
┌─ Step 3 ── Where does the endpoint live? ──────────────────────┐
│  ( ) On the Station's LAN     → prompt for LAN IP              │
│  ( ) On the Station PC itself → stationTarget = 127.0.0.1      │
│  ( ) Serial-attached to the Station PC → serial bridge branch  │
└────────────────────────────────────────────────────────────────┘
                              ↓
┌─ Step 4 ── Extras (multi-select) ──────────────────────────────┐
│  [ ] Rotator control (rotctld / PstRotator)                    │
│  [ ] Hamlib rigctld                                            │
│  [ ] Logger UDP broadcasts (N1MM+ / DXLog)                     │
│  [ ] Station PC remote desktop (RDP / VNC)                     │
│  [ ] Additional RS-232 device (repeats §7 sub-flow)            │
└────────────────────────────────────────────────────────────────┘
                              ↓
┌─ Step 5 ── Review & Apply ─────────────────────────────────────┐
│  Rule table, conflict report, and the manual steps that remain. │
│  [ ] Enable these rules immediately  (default: off)             │
│  [ ] Also write .vspe / com0com helper files                    │
│                                        [Cancel]  [Apply]        │
└────────────────────────────────────────────────────────────────┘
```

Step 3 is skipped when the catalog entry pins the answer (e.g. FlexRadio discovery
relay always implies a LAN device).

### 3.1 Explanatory copy in each step

Each step renders the catalog's `why` text for every input. Worked example — the LAN
IP prompt in Step 3, for an IC-7300MK2:

> **Radio's IP address on the Station's LAN**
> `192.168.1.___`
>
> *Why:* This is where the Station sends traffic after it comes out of the tunnel.
> The Station is already on the same LAN as your radio, so this is the radio's
> ordinary local address — not a Tailscale address, and not your public IP.
>
> <sub>▸ Where to find it — IC-7300MK2: MENU → SET → Network → IP Address. Set a
> fixed address rather than DHCP; if the radio's lease changes, these rules point at
> nothing.</sub>
>
> <sub>▸ If this is wrong — The rules will bind normally and the Status column will
> read "Listening". Nothing will report an error. Remote Utility will simply fail to
> connect, or hang at the login step.</sub>

The same structure applies to every prompt: the base port in the serial bridge, the
COM port numbers, the choice between `127.0.0.1` and `0.0.0.0` as bind address. The
bind address prompt in particular should explain the security consequence in plain
terms rather than just labelling the field.

---

## 4. Output JSON Schema

```json
{
  "rwkProfileVersion": 1,
  "generator": "RWK Wizard 1.0",
  "createdUtc": "2026-08-20T14:32:00Z",

  "profile": {
    "name": "Malawi — IC-7300MK2 via RS-BA1",
    "catalogId": "icom.rsba1.radio-lan",
    "confidence": "verified"
  },

  "setupNotes": {
    "client": [
      "Remote Utility → Server List: address 127.0.0.1, control port 50001",
      "Leave Server Setting blank on the client side"
    ],
    "station": [
      "No station-side software required — radio serves RS-BA1 directly"
    ],
    "radio": [
      "Set a fixed LAN IP on the radio (do not rely on DHCP)",
      "Register a Network User with a password in the radio's Network menu"
    ],
    "virtualSerial": []
  },

  "forwards": [
    {
      "name": "RSBA1-Control",
      "protocol": "UDP",
      "enabled": true,
      "bindAddress": "127.0.0.1",
      "clientPort": 50001,
      "stationTarget": "192.168.1.40",
      "stationPort": 50001,
      "portIdentity": "required",
      "role": "control",
      "notes": "Icom control / login channel"
    },
    {
      "name": "RSBA1-Serial",
      "protocol": "UDP",
      "enabled": true,
      "bindAddress": "127.0.0.1",
      "clientPort": 50002,
      "stationTarget": "192.168.1.40",
      "stationPort": 50002,
      "portIdentity": "required",
      "role": "cat",
      "notes": "CI-V"
    },
    {
      "name": "RSBA1-Audio",
      "protocol": "UDP",
      "enabled": true,
      "bindAddress": "127.0.0.1",
      "clientPort": 50003,
      "stationTarget": "192.168.1.40",
      "stationPort": 50003,
      "portIdentity": "required",
      "role": "audio",
      "notes": "Bidirectional audio"
    }
  ]
}
```

### 4.1 Field mapping to the existing Port Forwards grid

| JSON field | Grid column | Required |
|---|---|---|
| `name` | Name | yes |
| `protocol` | Protocol (`TCP` \| `UDP`) | yes |
| `enabled` | Enable state | no (default `false`) |
| `bindAddress` | Bind Address | no (default `127.0.0.1`) |
| `clientPort` | Client port | yes |
| `stationTarget` | Station Target | yes |
| `stationPort` | Station port | yes |
| `portIdentity`, `role`, `notes` | — | no (Wizard metadata) |

### 4.2 Apply — writing rules into the grid

On **Apply**, the Wizard writes rules directly into the Client's rule collection.

- **Merge by `name`.** A rule whose `name` matches an existing rule updates it in
  place. Re-running the Wizard for the same radio is therefore idempotent instead of
  producing a second set of duplicates. The review screen shows an
  add / update / unchanged count before the operator commits.
- **Rules land disabled** unless the operator ticks *Enable these rules immediately*.
  Default off. Opening listening sockets should be a deliberate act, and the operator
  usually still has radio-side or software-side configuration to do first.
- **Preserve hand-edits.** If an existing rule has a non-default `stationTarget` and
  the incoming one is a placeholder, keep the existing value. LAN IPs are the field
  operators hand-edit most, and silently reverting one is infuriating.
- **Persist immediately.** Rules are written to the normal config store in the same
  operation, so a crash between Apply and the next clean shutdown does not lose them.

### 4.3 Save — the JSON profile

Written to `%LOCALAPPDATA%\RWK Router Keyer\profiles\[radioname].rwkprofile.json`,
with a *Save As…* option on the review screen.

The saved profile is a portable artifact, not a working file — it exists so an
operator can hand their configuration to someone else with the same radio, restore it
on a replacement PC, or attach it to a support request. Loading one runs the same
merge path as Apply.

Loading also needs the guards a separate importer would have needed:

- **Version gate.** Reject `rwkProfileVersion` greater than the app understands, with
  a message naming the version. Accept lower versions.
- **Ignore unknown keys.** The Wizard will always be ahead of any older Client that
  might load the file. Unknown fields are discarded, never treated as errors.
- **Re-run conflict detection** against the loading machine's state. A profile that
  was clean on the machine that produced it may collide on another.

### 4.4 Undo

Because Apply mutates live configuration, snapshot the full rule collection
immediately before writing and keep it for the session. The Port Forwards panel shows
**Undo wizard changes** until the operator makes any manual edit to the grid, at
which point the snapshot is discarded rather than risk reverting their work.

This matters most for the collision cases in §8.3, where the operator may need two or
three attempts before the numbering comes out right.

---

## 5. Catalog Schema (`radios.json`)

```json
{
  "catalogVersion": 3,
  "updated": "2026-08-20",
  "entries": [
    {
      "id": "kenwood.kns.direct",
      "vendor": "Kenwood",
      "displayName": "KNS direct to radio (ARCP-890)",
      "models": ["TS-890S"],
      "software": "ARCP-890",
      "endpointLocation": "lan",
      "confidence": "verified",
      "source": "https://www.kenwood.com/i/products/info/amateur/ts_890/pdf/ts890_kns_manual_e.pdf",
      "forwards": [
        { "proto": "TCP", "port": 60000, "role": "control",
          "portIdentity": "required", "fixed": true },
        { "proto": "UDP", "port": 60001, "role": "audio",
          "portIdentity": "required", "fixed": true }
      ],
      "prompts": {
        "stationTarget": {
          "label": "TS-890S IP address on the Station's LAN",
          "why": "The Station sends control and audio here after traffic leaves the tunnel. This is the radio's ordinary LAN address — not a Tailscale address.",
          "howToFind": "On the radio: LAN menu → IP Address. Set it manually; a DHCP lease change will silently break these rules.",
          "ifWrong": "Both rules bind and show 'Listening'. ARCP-890 fails to connect with no useful diagnostic."
        }
      },

      "clientNotes": [
        "Connection type: KNS (Directly to TS-890S)",
        "IP address: 127.0.0.1",
        "Leave the MAC address field BLANK — use the Internet-mode configuration, not the LAN-mode one",
        "Tick 'Use TS-890S Built-in VoIP'"
      ],
      "radioNotes": [
        "KNS menu 1: KNS Operation (LAN Connector) = On (Internet)",
        "KNS menu 3: Built-in VoIP = On",
        "Modulation Source: DATA SEND audio input = LAN"
      ]
    }
  ]
}
```

`fixed: true` means the port cannot be changed *at the device*, which is stronger than
`portIdentity: required` — it also rules out renumbering as a collision remedy.

`prompts` is keyed by input name. Any prompt without a catalog entry falls back to
generic copy held in the Wizard itself, so an incomplete catalog entry degrades to a
plain field rather than an empty one. Catalog contributions should be encouraged to
fill in `howToFind` with the exact menu path for their model — that is the single
most valuable thing a community contributor can add, and it is the thing no vendor
manual presents in a form an operator can act on quickly.

---

## 6. Seed Catalog

### 6.1 Icom — RS-BA1 Version 2

Three UDP ports, <cite index="11-1">50001 for control, 50002 for the serial port, and 50003
for audio, all defaults</cite>. <cite index="6-1">Icom's own installation guide instructs
operators to forward exactly 50001, 50002 and 50003</cite>.

| id | Path | Rules | Station Target | Confidence |
|---|---|---|---|---|
| `icom.rsba1.radio-lan` | Radio's own LAN port, no server PC | UDP 50001/50002/50003 | Radio LAN IP | verified |
| `icom.rsba1.server-pc` | RS-BA1 Remote Utility server on a station PC | UDP 50001/50002/50003 | Server PC LAN IP, or 127.0.0.1 if that PC *is* the RWK Station | verified |

`portIdentity: required` on all three, for the reason in §2.2.

**Radios with a built-in LAN port (use `radio-lan`):** IC-7300MK2, IC-7610, IC-9700,
IC-7851, IC-905, IC-R8600. The IC-705 uses WLAN and behaves the same way.
The IC-7300MK2 is the notable recent addition — <cite index="49-1">its built-in LAN port allows
PC-less remote operation with RS-BA1 Version 2, including remote power ON/OFF</cite>.

**Radios needing a server PC (use `server-pc`):** original IC-7300, IC-7100, IC-9100,
IC-7600, IC-7700, and any CI-V/USB-only Icom. Note that RS-BA1 also supports
<cite index="11-1">RS-232C control of transceivers without a LAN, WLAN or USB port, though audio
through the ACC socket, MIC connector or S/P DIF jack is not guaranteed</cite>.

**Client checklist:**
- Remote Utility → Server List: address `127.0.0.1`, control port `50001`
- Do **not** fill in anything under Server Setting on the client
- The Radio List populates itself on first successful connect
- If the radio is behind `server-pc`, the Remote Utility server must be *running* on
  the station PC — it is a foreground program, not a Windows service

**Known collision:** these ports overlap AnyDesk, which
<cite index="3-1">uses UDP 50001–50003 for local-network discovery</cite>, and overlap Yaesu's
SCU-LAN10 default range. See §8.3.

---

### 6.2 Icom — native LAN protocol (wfview, Win4Icom)

Same UDP 50001/50002/50003 triple against the radio's LAN IP; wfview speaks the
radio's native protocol directly rather than going through Remote Utility. Same
`portIdentity: required`.

| id | Software | Confidence |
|---|---|---|
| `icom.native.wfview` | wfview | community |

Client checklist: wfview → Radio Access = Network, hostname `127.0.0.1`, ports
50001/50002/50003, radio username and password as set in the radio's Network menu.

---

### 6.3 Kenwood — KNS direct (built-in VoIP)

For the TS-890S the radio itself is the server. Kenwood's KNS manual specifies
<cite index="21-1">TCP port 60000 for control data and UDP port 60001 for audio data</cite>, and
the FAQ in the same manual is explicit that
<cite index="21-1">the port numbers used by the TS-890S cannot be changed</cite>.

| id | Rules | Station Target | Confidence |
|---|---|---|---|
| `kenwood.kns.direct` | TCP 60000 control, UDP 60001 audio | Radio LAN IP | verified |

`portIdentity: required`, `fixed: true`.

**Two important client-side notes:**

1. In ARCP-890, choose the *Internet* connection configuration, not the LAN one.
   <cite index="21-1">The LAN configuration requires the radio's MAC address, while the Internet
   configuration instructs you to leave the MAC address field blank</cite>. Through
   an RWK tunnel the radio appears at `127.0.0.1`, so the Internet configuration is
   the correct model.
2. Kenwood's own documentation also asks operators to open UDP 60001 *at the remote
   station*, because audio flows back toward the client. RWK's tunnel handles the
   reverse path, so no client-side router change is needed — but this is why the
   audio rule must be UDP, not TCP.

---

### 6.4 Kenwood — conventional system (ARHP host program)

The older architecture: a host PC on the station LAN runs ARHP-*nnn* and ARVP-10H;
the client runs ARCP-*nnn* and ARVP-10R. The TS-890 manual gives the defaults for
this path as <cite index="21-1">TCP 50000 for control data and UDP 33550 for audio data, both
directed at the host PC</cite>.

| id | Rules | Station Target | Confidence |
|---|---|---|---|
| `kenwood.arhp.conventional` | TCP 50000 control, UDP 33550 audio | Host PC LAN IP (or 127.0.0.1) | verified |

`portIdentity: floating` for the control port — ARCP asks for the ARHP port number
explicitly ("ARHP-890 Port No. for PC command"), so it can be remapped. The ARVP-10
audio port is `unknown`; treat as required until someone tests it.

**Radio → host program pairing** (each ARHP is model-locked; the manual is emphatic
about this):

| Radio | Host program | Control program |
|---|---|---|
| TS-480HX / TS-480SAT | ARHP-10 | ARCP-480 |
| TS-590S | ARHP-590 | ARCP-590 |
| TS-590SG | ARHP-590G | ARCP-590G |
| TS-890S | ARHP-890 | ARCP-890 |
| TS-990S | ARHP-990 | ARCP-990 |
| TS-2000 / TS-2000X / TS-B2000 | — | ARCP-2000 |

The Wizard should surface this mapping directly, because using the wrong ARHP is a
common failure and produces an unhelpful error.

**Audio caveat worth printing on the checklist:** in the conventional system the host
PC takes audio from the radio's ACC 2 connector over an analogue cable. If the
operator has no audio cable installed, no amount of port forwarding will help.

---

### 6.5 Yaesu — SCU-LAN10

<cite index="22-1">Communication uses UDP ports 50000–50003 as the factory setting</cite>, and
<cite index="23-1">the installation manual describes opening four UDP ports by default</cite>.

| id | Rules | Station Target | Confidence |
|---|---|---|---|
| `yaesu.sculan10` | UDP 50000, 50001, 50002, 50003 | SCU-LAN10 LAN IP | verified |

`portIdentity: required` pending test; the base port is configurable in the SCU-LAN10
Setting Tool, so collisions can be resolved by moving the whole block — but the block
must move on *both* sides together.

**On CGNAT:** yes, this is a strong RWK use case. The SCU-LAN10 normally demands
<cite index="22-1">a fixed global IP address or fixed domain name for remote control over an
Internet line</cite>, which is precisely what a CGNAT, satellite or cellular
subscriber cannot get. Tunnelling the four UDP ports over the Tailscale mesh removes
that requirement entirely — the Remote Software points at `127.0.0.1` and never
learns it isn't on the same LAN as the interface. This is the same argument as the
FlexRadio/SmartLink case and deserves the same prominence in the docs.

**Supported radios:** FTDX101MP/D, FTDX10, FT-710 and other Yaesu models on the
current compatibility list. The Wizard should link to Yaesu's list rather than
hardcode it, since it grows.

---

### 6.6 FlexRadio 6000 / 8000 — SmartSDR

| id | Rules | Station Target | Confidence |
|---|---|---|---|
| `flex.smartsdr` | TCP 4992 command, UDP 4991 VITA-49 streaming | Radio LAN IP | verified |

`portIdentity: floating` — SmartSDR tells the radio where to send streams via the
`client udpport` command rather than assuming a fixed port, which is why Flex tolerates
remapping when the others don't.

Requires `requiresDiscoveryRelay: true`: the Wizard must instruct the operator to tick
*Enable discovery capture* on the Station and *Enable discovery re-emission* on the
Client. Without those, the port forwards are correct but SmartSDR never sees the radio.

This is the one entry where the Wizard's output alone is insufficient — the checklist
must lead with the discovery relay steps.

---

### 6.7 Elecraft K4 / K4/0

The K4 acts as its own peer-to-peer server; no third-party software or interface is
required. The remote server listens on **TCP 9205**, which is the port operators are
told to open for the server K4. Elecraft's firmware notes also reference
<cite index="30-1">ports 9204 and 9205 in the context of a K4 acting as a client, a configuration
that is no longer accepted</cite>.

| id | Rules | Station Target | Confidence |
|---|---|---|---|
| `elecraft.k4.remote` | TCP 9205 | K4 LAN IP | verified |

`portIdentity: unknown` → treat as required.

**One rule is sufficient.** QK4 (Virtual K4) specifies a single port, and the K4/0
hardware panel is configured the same way — the peer-to-peer session carries command
traffic, audio, and panadapter data multiplexed inside the one TCP connection. The
separate per-stream ports described in the Programmer's Reference are for developers
opening raw streams directly, not for the normal remote client path.

This makes the K4 the simplest entry in the catalog: one TCP rule, no discovery relay,
no audio side-channel. Worth calling out in the docs, since operators arriving from
the Icom or Yaesu world expect three or four rules and will assume something is
missing.

Also worth a catalog entry: `elecraft.k4.cat-serial`, the K4's rear-panel RS-232 CAT
port routed through the generic serial bridge in §7, for operators who only want
logger CAT and are keying via RWK anyway.

---

### 6.8 RemoteRig RRC-1258 MkII

Current firmware defaults are <cite index="39-1">13000 for SIP, 13001 for RTP and 13002 for
CMD</cite>, with <cite index="41-1">13002 carrying the data channel and control commands, plus
the web and telnet interfaces; earlier firmware used 12000, 11000, 5060, 80 and
23</cite>. RemoteRig's manual asks operators to
<cite index="40-1">direct ports 13000, 13001, 13002 and 80 to the Radio-RRC, which should have a
static IP address</cite>. The 10-port antenna switch adds
<cite index="39-1">port 13010</cite>.

| id | Rules | Station Target | Confidence |
|---|---|---|---|
| `remoterig.rrc1258.mk2` | UDP 13000 SIP, UDP 13001 RTP, UDP 13002 CMD, TCP 80 web (optional), UDP 13010 antenna switch (optional) | Radio-RRC LAN IP | verified |
| `remoterig.rrc1258.legacy` | UDP 5060, 11000, 12000 + TCP 80 | Radio-RRC LAN IP | community |

`portIdentity: required` — SIP carries endpoint information in its own payload.

**This entry needs `bindAddress: 0.0.0.0`, not `127.0.0.1`.** The Control-RRC at the
operating position is a hardware box on the client's LAN, not software on the RWK
Client PC. It has to be pointed at the RWK Client PC's LAN address. The Wizard must
special-case this and warn about the LAN exposure that implies.

Also flag on the checklist: <cite index="40-1">SIP ALG must not be active on the router</cite>.
Tunnelling over Tailscale sidesteps most of that, but a client-side router with SIP ALG
mangling packets before they reach RWK is still a live failure mode.

---

### 6.9 Ancillary services (Step 4 extras)

These are add-on rules, not radio entries. All `portIdentity: floating` unless noted.

| id | Service | Protocol / port | Confidence |
|---|---|---|---|
| `svc.rigctld` | Hamlib `rigctld` | TCP 4532 | verified |
| `svc.rotctld` | Hamlib `rotctld` | TCP 4533 | verified |
| `svc.pstrotator` | PstRotator TCP server | TCP, user-configured | unverified |
| `svc.n1mm.broadcast` | N1MM+ / DXLog UDP broadcasts | UDP 12060 | community |
| `svc.rdp` | Station PC remote desktop | TCP 3389 | verified |
| `svc.vnc` | Station PC VNC | TCP 5900 | verified |
| `svc.http` | Device web UI (RRC, switch, PDU) | TCP 80 / 8080 | n/a |

For `svc.n1mm.broadcast`, note that N1MM+ broadcasts to a *list* of addresses
configured in the logger, and the datagrams are one-way. The Wizard should mention
that the sending side needs `127.0.0.1:12060` added to its broadcast list rather than
relying on subnet broadcast, which will not traverse the tunnel.

---

### 6.10 Not yet supported — needs a discovery relay

**OpenHPSDR / ANAN / Hermes-Lite (Thetis, piHPSDR, PowerSDR).** These radios are
discovered by a UDP broadcast on port 1024 and then stream on negotiated UDP ports,
structurally similar to the FlexRadio case. Plain port forwarding will not work: the
broadcast has to be captured at the Station, relayed, and re-emitted on the Client
LAN with the endpoint rewritten — exactly what RWK's FlexRadio discovery relay does
for VITA-49.

The Wizard should list this as a known-unsupported entry with an explanatory note
rather than silently omitting it, so operators stop trying. Generalising the existing
discovery relay to a pluggable codec (Flex VITA-49 today, HPSDR tomorrow) is the
natural follow-on, and is tracked separately.

---

## 7. Generic RS-232 Tunnelling

This is the universal fallback: any CAT-controllable radio, rotator controller,
antenna switch, amplifier, SteppIR controller or SDR that speaks serial can be reached
this way. It is also the entry the Wizard should push operators toward when their
radio isn't in the catalog.

### 7.1 Topology

```
CLIENT PC                         RWK TUNNEL                 STATION SIDE
─────────                         ──────────                 ────────────
Logger / control software
   │  writes to COM20 (virtual)
   ▼
VSPE "TcpClient" device
   │  connects to 127.0.0.1:4000
   ▼
RWK Client forward  ──────────────────────────────►  RWK Station
   bind 127.0.0.1                                        │
   clientPort 4000                                       ▼
   stationPort 4000                          VSPE "TcpServer" device
   stationTarget 127.0.0.1                     listening 0.0.0.0:4000
                                               data source: real COM3
                                                          │
                                                          ▼
                                                    Radio CAT port
```

Generated rule:

```json
{
  "name": "CAT-Serial-Bridge",
  "protocol": "TCP",
  "enabled": true,
  "bindAddress": "127.0.0.1",
  "clientPort": 4000,
  "stationTarget": "127.0.0.1",
  "stationPort": 4000,
  "portIdentity": "floating",
  "role": "cat",
  "notes": "VSPE TcpClient COM20 ↔ VSPE TcpServer COM3"
}
```

### 7.2 Wizard sub-flow

1. **What is the device?** (free text — becomes the rule name)
2. **Station side:** which COM port on the Station PC, or a LAN serial device server
   (Digi, Moxa, `ser2net` on a Pi) with its IP and port
3. **Baud rate, data bits, parity, stop bits** — needed for the generated config files
4. **Client side:** which virtual COM port number to present to the logger
5. **Does the device need RTS or DTR?** (see §7.4 — this changes the recommendation)

Base TCP port defaults to 4000 and increments per additional device.

### 7.3 Generated helper files

`.vspe` files are XML. The Wizard can emit both ends, turning the fiddliest part of
the setup into two double-clicks:

- `<profile>-client.vspe` — TcpClient device, virtual port COMnn, target `127.0.0.1:port`
- `<profile>-station.vspe` — TcpServer device, listening on `0.0.0.0:port`, data source
  the real COM port, with matching line settings

**Licensing note the Wizard should surface:** VSPE's 64-bit driver requires a paid
licence. The free alternative is **com0com** (virtual port pairs) plus **com2tcp** /
**hub4com**, which ship with it. The Wizard should offer com0com command lines as an
alternative output:

```
:: Station side — real COM3 to TCP listener on 4000
com2tcp --baud 38400 --parity n --data 8 --stop 1 \\.\COM3 4000

:: Client side — create pair CNCA0/CNCB0, then bridge CNCB0 to the tunnel
com2tcp --baud 38400 --parity n --data 8 --stop 1 \\.\CNCB0 127.0.0.1 4000
:: logger uses CNCA0
```

On Linux/Raspberry Pi stations, `ser2net` is the cleanest option and the Wizard should
emit a `ser2net.yaml` fragment.

### 7.4 The modem-control-line trap

**A plain TCP serial bridge does not carry RTS, DTR, CTS or DSR.** Only the data
stream crosses. This matters enormously and is the single most common source of
"CAT works but PTT doesn't" reports.

The Wizard must ask about this explicitly and branch:

| Device needs | Recommendation |
|---|---|
| Data only (CI-V, Kenwood/Yaesu ASCII CAT, rotator protocols) | Plain bridge is fine |
| RTS/DTR for PTT | Use RWK's own keying output, or a CAT-command PTT (`TX;` / `FEFE…1C00…`), or RFC 2217 |
| RTS/DTR for CW keying | **Use RWK's keyer.** This is the whole point of the project — a jittered TCP bridge will destroy CW timing. |

For RFC 2217 (telnet COM port control option), `com2tcp --telnet` supports it, as does
`ser2net`. Support on the client side is spottier. The Wizard should present it as an
advanced option with a warning rather than a default.

### 7.5 Latency guidance for the checklist

CAT polling that is comfortable on a local USB cable can misbehave across a tunnel,
especially on a DERP path. The generated checklist should recommend:

- Raise the logger's CAT poll interval to 500 ms or more
- Disable any "verify every command" / read-back option
- For Icom CI-V, disable transceive mode if the logger supports polling instead —
  unsolicited CI-V traffic multiplies badly with round-trip latency
- Expect 1–5 ms added latency on a direct Tailscale path, 20–50 ms on DERP

---

## 8. The Generated Readme

### 8.1 Purpose and lifecycle

`[radioname]-readme.txt` is the document the operator actually uses. The rules are in
the grid and need no further attention; the readme covers everything the Wizard
*cannot* do for them — radio menu settings, control-software configuration, and any
manual step flagged during conflict detection.

It is written on Apply and **opened immediately** in the default text editor. Opening
it automatically is the point: a file written silently to a profiles directory is a
file nobody reads, and the steps it contains are prerequisites, not reference
material.

### 8.2 Naming and location

- Filename: the profile name, sanitised — strip everything outside
  `[A-Za-z0-9._-]`, collapse runs of whitespace to a single `-`, trim to 64
  characters. `Malawi — IC-7300MK2 via RS-BA1` → `Malawi-IC-7300MK2-via-RS-BA1-readme.txt`.
- Location: alongside the JSON profile in
  `%LOCALAPPDATA%\RWK Router Keyer\profiles\`.
- Collision: overwrite, after confirming. The file is fully regenerable, and
  accumulating `-readme (3).txt` variants helps nobody. The confirm prompt exists only
  because an operator may have annotated the previous copy.

### 8.3 Format constraints

This file opens in Notepad. That imposes real constraints that a Markdown document
does not have:

- **CRLF line endings.** Non-negotiable. Notepad on Windows 10 builds before 1809
  renders LF-only files as one continuous line.
- **No Markdown syntax.** No `##`, no `|` tables, no backticks. Use ASCII rules,
  indentation, and numbered steps.
- **Hard-wrap at 76 columns.** Notepad does not word-wrap by default.
- **UTF-8 with BOM**, or plain ASCII. Prefer ASCII — transliterate `→` to `->` and
  em-dashes to `--`. A mangled character in a menu path is worse than an ugly one.

### 8.4 Launching the editor

`Process.Start` with `UseShellExecute = true` on the file path. Three failure modes to
handle rather than let throw:

- No handler registered for `.txt` — fall back to `notepad.exe <path>` explicitly.
- Shell execute fails entirely (locked-down machine, policy) — show the path in a
  dialog with a *Copy path* and *Open containing folder* button.
- Never block the UI thread waiting on the launch, and never wait for the editor to
  exit.

The review screen keeps an **Open setup guide** button regardless, so the operator can
get back to it after closing the window.

### 8.5 Content and ordering

Ordered by when the operator needs it, not by system layer. Everything that must
happen before a connection will succeed comes first.

```
================================================================
 RWK PORT FORWARD SETUP
 Malawi -- IC-7300MK2 via RS-BA1
 Generated 2026-08-20 14:32 UTC by RWK Wizard 1.0
================================================================

WHAT THIS DOES
--------------
Your IC-7300MK2 sits on the LAN at your remote station. These port
forwards make it appear on this PC at 127.0.0.1, so Icom's Remote
Utility connects to it as though it were plugged in here. No public
IP address, no dynamic DNS, no router configuration at either end.

BEFORE YOU CONNECT -- 3 things still need doing
-----------------------------------------------

1. ON THE RADIO
   MENU -> SET -> Network
     - IP Address: set a FIXED address (currently 192.168.1.40).
       If this is left on DHCP and the lease changes, the forwards
       below will point at nothing and fail silently.
     - Network User: create a username and password. Note them; you
       need them in step 3.

2. ON THIS PC -- Icom Remote Utility
   Server List -> Add
     - Address:      127.0.0.1
     - Control port: 50001
   Leave "Server Setting" completely blank. That section is for
   machines acting as a base station; this PC is a client.
   The Radio List fills itself in on the first successful connect.

3. ENABLE THE RULES
   The rules were created but left disabled. In the Port Forwards
   panel, select all three and click "Enable Sel". Status should
   read "Listening".

RULES CREATED
-------------
   Name             Proto  Local        -> Station
   RSBA1-Control    UDP    127.0.0.1:50001 -> 192.168.1.40:50001
   RSBA1-Serial     UDP    127.0.0.1:50002 -> 192.168.1.40:50002
   RSBA1-Audio      UDP    127.0.0.1:50003 -> 192.168.1.40:50003

   These three ports must stay identical on both sides. RS-BA1 takes
   the CAT and audio port numbers from the server's own settings
   rather than asking for them here, so renumbering one side alone
   produces "Virtual serial device error". Do not change them.

WARNINGS FROM SETUP
-------------------
   * AnyDesk uses UDP 50001-50003 for local discovery. If AnyDesk is
     installed on this PC, change its port range or these rules will
     not bind.

IF IT DOES NOT WORK
-------------------
   Status stuck at "Listening", client times out
     -> Almost always the radio's LAN IP. Confirm 192.168.1.40 is
        still correct and reachable from the Station PC.
   "Virtual serial device error"
     -> Port numbers differ between the two ends. See above.
   Connects, no audio
     -> Radio-side audio settings, not a network problem.

   RWK status bar shows path type (Direct or DERP) and RTT. If it
   reads DERP, expect 20-50 ms more latency; this is normal and does
   not affect CAT control.

================================================================
 Profile saved: Malawi-IC-7300MK2-via-RS-BA1.rwkprofile.json
 Re-run the wizard at any time; it updates these rules in place.
================================================================
```

The `IF IT DOES NOT WORK` block is assembled from the `ifWrong` copy already in the
catalog (§2.4), so it costs nothing extra to maintain and stays consistent with what
the operator was told during the wizard.

---

## 9. Conflict Detection

Run before the review screen. Errors block Apply; warnings do not.

Running in-process makes three checks possible that a standalone tool could only
guess at, and these catch the majority of real failures:

- **Against the existing rule set.** The Wizard sees every rule already configured,
  including ones the operator added by hand, and can report an exact collision rather
  than a hypothetical one.
- **Against live sockets.** Attempt a trial bind on each proposed local endpoint. If
  something else on the machine already holds it — AnyDesk on 50001, an old RWK rule
  still listening — say so by name where the owning process can be identified.
- **Against Station reachability.** If the Station is paired and connected, the
  Wizard can ask it whether `stationTarget` responds to a probe before the operator
  ever leaves the dialog. This turns the single most common failure (wrong radio LAN
  IP, silent timeout) into an immediate, specific error.

The reachability probe must be optional and must not block Apply — a radio powered
off during setup is normal, and the Wizard should say "could not reach 192.168.1.40
from the Station; this may be correct if the radio is off" rather than refusing.

### 9.1 Errors

| Check | Message |
|---|---|
| Duplicate `protocol` + `bindAddress` + `clientPort` | Two rules would bind the same local endpoint |
| `portIdentity: required` and `clientPort != stationPort` | Protocol requires matching ports |
| `clientPort` or `stationPort` outside 1–65535 | Invalid |
| `stationTarget` unparseable as IPv4/hostname | Invalid |
| `stationTarget` is a Tailscale address (100.64.0.0/10) | Station Target is a LAN device, not the Station itself |

### 9.2 Warnings

| Check | Message |
|---|---|
| `bindAddress: 0.0.0.0` | Rule is reachable from your entire local network |
| Port inside a Windows excluded range | May fail to bind; check `netsh int ipv4 show excludedportrange` |
| Port in the ephemeral range (49152–65535) | May collide with an OS-assigned port |
| Port used by a known non-radio application | e.g. AnyDesk on 50001–50003 |
| More than ~8 UDP rules | Consider whether all are actually needed |

### 9.3 Same-port collisions across profiles

Adding a second radio that wants ports already in use is the interesting case, and the
Wizard's response depends on `portIdentity`:

- **`floating`** — offer to renumber the client side. One click, done.
- **`required` but not `fixed`** — the block must move on *both* sides. Walk the
  operator through changing it at the source (Icom Remote Utility's Network Setting;
  the SCU-LAN10 Setting Tool) and regenerate both ports together.
- **`required` and `fixed`** — renumbering is impossible. Offer distinct loopback bind
  addresses instead: `127.0.0.2`, `127.0.0.3`, and so on.

  **Windows caveat:** unlike Linux, Windows does not answer on all of 127.0.0.0/8 by
  default. The extra addresses have to be added to the loopback interface first:

  ```
  netsh interface ipv4 add address "Loopback Pseudo-Interface 1" 127.0.0.2 255.0.0.0
  ```

  This requires administrator rights, which is contrary to RWK's no-admin design goal.
  The Wizard should therefore present this as a documented manual step with the exact
  command, not attempt it itself.

The concrete case operators will hit first: **Icom RS-BA1 (50001–50003) versus Yaesu
SCU-LAN10 (50000–50003)**. Both are `required` but not `fixed`, so the remedy is to
move one block — the SCU-LAN10's base port is settable in its Setting Tool, and Icom's
is settable in Remote Utility's Network Setting, so either can move.

---

## 10. Roadmap

**v1.0** — Wizard flow with per-input explanatory copy, direct rule creation, JSON
profile save, `[radioname]-readme.txt` generation and auto-open, undo, seed catalog
covering §6, conflict detection including trial bind and Station reachability probe,
generic serial bridge with `.vspe` and com0com output.

**v1.1** — Catalog auto-update from the GitHub raw URL with a local override file;
"report a correction" link that pre-fills a GitHub issue with the operator's profile
(sanitised of IP addresses).

**v1.2** — Deeper protocol validation: not just "does the port answer" but "does it
answer the way an RS-BA1 control port should". Turns the Wizard into a diagnostic the
operator can re-run against an existing setup that has stopped working.

**v1.3** — Generalise the discovery relay to a pluggable codec so OpenHPSDR/ANAN,
and any future broadcast-discovery radio, can be added as catalog data rather than
as code.

**v2.0** — Reverse direction: read an existing RWK configuration and explain it —
"these three UDP rules look like an Icom RS-BA1 setup; here is what should be
configured at each end."

---

## Appendix A — Confidence Summary

| Entry | Confidence | Basis |
|---|---|---|
| `icom.rsba1.*` | verified | Icom RS-BA1 manual and installation guide |
| `icom.native.wfview` | community | Field reports; same port triple as RS-BA1 |
| `kenwood.kns.direct` | verified | Kenwood TS-890S KNS Setting Manual |
| `kenwood.arhp.conventional` | verified | Kenwood TS-890S KNS Setting Manual §5.6.2 |
| `yaesu.sculan10` | verified | Yaesu SCU-LAN10 Installation Manual |
| `flex.smartsdr` | verified | SmartSDR / existing RWK discovery relay implementation |
| `elecraft.k4.remote` | verified | Elecraft firmware notes plus QK4 / Virtual K4 field use — single TCP port confirmed |
| `remoterig.rrc1258.*` | verified | RemoteRig firmware changelog and RRC-1258 MkII user manual |
| Ancillary services | mixed | Hamlib defaults are verified; PstRotator and N1MM need confirmation |

Entries below `verified` display a banner in the Wizard inviting the operator to
report back.

---

*73 de W1VE*
