# Test fixtures — FlexRadio 6000-series discovery datagram

**Status: NOT CAPTURED. Requirement 15.20 is not satisfied.**

This directory is the home of one hardware capture that the discovery payload codec
(task 27.2, design Component 12 `DiscoveryPayloadCodec`) is built and tested against.
The capture does not exist yet. It cannot be produced without a physical FlexRadio
6000-series radio on a real network, so it must be taken by the station owner and checked
in here.

Until it lands:

- Requirement **15.20** ("the payload field layout SHALL be verified against a
  Discovery_Datagram captured from a physical FlexRadio 6000-series radio, and the test
  suite SHALL include that captured datagram as a fixture") is **not satisfied**.
- The codec produced by task **27.2 is provisional**. Every layout constant in it — byte
  offsets, field ordering, encodings, length prefix or checksum handling, and the broadcast
  port number — is marked `[VERIFY]` and is a guess until this fixture confirms it.
- Task **27.3** (codec unit tests driven by the fixture) cannot be completed.
- The release notes must list the FlexRadio payload layout as unverified (16.20).
- `DiscoveryFixturePresenceTests` fails on purpose, so this provisional state is loud in CI
  rather than silent. Those failures are the tracking mechanism. Do not skip or delete them
  to get a green build — they go green when the capture arrives.

## Why a real capture, and not a plausible-looking synthetic one

Design `design.md` → "FlexRadio Discovery Broker Components" → **Protocol accuracy note**
states the premise and its limit: a 6000-series radio periodically emits a UDP broadcast
whose payload carries the radio's own IP address and command port. That much is the design
premise. The concrete field layout is **not treated as established fact anywhere in the
design**, and every concrete value in `design.md` is marked *[VERIFY]*.

All layout knowledge is deliberately confined to the single `IDiscoveryPayloadCodec`
implementation precisely so that correcting it once this fixture exists is a **one-file
change**. Nothing outside that file — not `DiscoveredRadio`, not the listener, not the
emitter, not the config — may encode a layout assumption.

A fabricated fixture would defeat that entirely. The codec would be built against invented
offsets, its tests would pass against the same invention, and the discovery feature would
fail against real hardware with the whole suite green. An absent fixture and a failing test
is a far better state than a confident wrong one.

## Files this directory expects

| File | Required | What it is |
| --- | --- | --- |
| `flexradio-6000-discovery.bin` | yes | The datagram **body**, byte-for-byte, nothing else |
| `flexradio-6000-discovery.metadata.json` | yes | Observed capture facts and expected parse results |
| `flexradio-6000-discovery.metadata.template.json` | already here | Template to copy; placeholders are `null` |
| `README.md` | already here | This file |

The names are fixed. `DiscoveryFixture` in
`tests/RWK.Shared.Tests/Discovery/DiscoveryFixture.cs` resolves exactly these paths, so
dropping the two files in here is all that is required — no code change, no csproj change.

## How to perform the capture

Do this **on the Station host**, on the Station LAN — the network segment the radio is
actually on. The Client side is the wrong side: what arrives there is a rewritten payload,
which is the thing under test, not the input to it.

1. Have the radio powered on and idle on the Station LAN. It broadcasts periodically on its
   own, so no SmartSDR interaction is needed. Leaving SmartSDR closed keeps the capture
   free of command-channel traffic.
2. Install [Wireshark](https://www.wireshark.org/) on the Station host (or use `tshark`, or
   `pktmon` on Windows, or `tcpdump` on a Linux host on the same segment).
3. Select the interface facing the Station LAN. If the host has several NICs, pick the one
   holding the address in the radio's subnet, not a Tailscale or virtual adapter.
4. Apply a capture filter that keeps broadcast UDP only:

   ```
   udp and (ip broadcast or ip multicast)
   ```

   If you already know the radio's address, narrow it further and be certain:

   ```
   udp and src host <radio-ip>
   ```

   Do **not** filter on a guessed port number. The broadcast port is one of the things this
   capture establishes; filtering on a guess can silently exclude the very datagram wanted.
5. Let it run for at least 30 seconds so several periodic broadcasts are recorded. Confirm
   the repeats look identical apart from any counter or timestamp field — that repetition is
   itself useful evidence about the layout.
6. Pick one datagram from the radio. In Wireshark, expand the packet detail tree and select
   the **`Data` / UDP payload** node — the bottom-most node, below Frame, Ethernet, IPv4 and
   UDP.
7. Right-click that node → **Export Packet Bytes…** and save as
   `flexradio-6000-discovery.bin` in this directory.

### Only the datagram body

The fixture must be the **UDP payload alone**, byte-for-byte. It must not contain the
Ethernet header, the IP header, or the UDP header. Getting this wrong shifts every offset
the codec derives by 42 bytes and the mistake is not obvious from the hex.

Two checks that it is right:

- The file size equals the UDP `Length` field shown in Wireshark minus 8 (the UDP header),
  and equals the `datagramLengthBytes` you record in the metadata file.
- The first bytes are payload content, not `45 00` (an IPv4 header) and not a MAC address.

Also: export the **packet bytes of that one node**, not "File → Export Specified Packets",
which writes a `.pcap` container rather than raw bytes. A `.pcap` is fine to keep alongside
for reference, but it is not the fixture.

### What to record in the metadata file

Copy `flexradio-6000-discovery.metadata.template.json` to
`flexradio-6000-discovery.metadata.json` and fill in every `null`. The template documents
each field inline. In summary:

- **observedBroadcastPort** — the UDP destination port of the captured datagram. This is the
  port the Station listener binds and the port SmartSDR listens on; it is `[VERIFY]` in
  `DiscoveryListenerConfig.ListenPort` and `DiscoveryEmitterConfig.BroadcastPort` until
  recorded here.
- **datagramLengthBytes** — the length of the body, which must equal the size of the `.bin`.
- **sourceAddress** / **sourcePort** — the radio's address and source port as seen on the
  wire. `sourceAddress` is the value that must appear inside the payload; that
  correspondence is the anchor for locating the address field.
- **destinationAddress** — the broadcast address observed (for example `255.255.255.255` or
  the subnet-directed broadcast).
- **radioModel**, **radioSerial**, **firmwareVersion** — the model as printed on the radio
  and shown in SmartSDR, the serial number, and the SmartSDR / radio firmware version the
  capture came from. Firmware matters: a later revision may add fields, and the codec is
  required to preserve bytes it does not interpret so that adding fields does not break
  brokering.
- **capturedUtc**, **captureTool**, **capturedBy** — provenance, so a future contributor can
  judge whether a fresh capture is warranted.
- **expectedParse** — the values `IDiscoveryPayloadCodec.TryParse` must return for this
  payload: `serial`, `model`, `stationAddress`, `stationCommandPort`. These are the
  assertions task 27.3 makes, so they must be read off the radio and SmartSDR, independently
  of whatever the codec happens to produce. Filling these in from the codec's own output
  would make the test tautological.

`stationCommandPort` is the command port the radio advertises **inside** the payload. It is
not the same thing as `observedBroadcastPort`, which is the transport-level destination port
of the discovery broadcast itself. Both are needed and they are different numbers.

## After the capture lands

1. The two `DiscoveryFixturePresenceTests` go green with no code change.
2. Task 27.2: derive every layout constant in the codec from the fixture, and drop the
   `[VERIFY]` marker from each constant the fixture actually confirms. Anything the fixture
   does not confirm — a checksum algorithm, a field only a different model emits — stays
   `[VERIFY]`.
3. Task 27.3: write the codec unit tests against `expectedParse`, plus the rewrite
   round-trip and the negative cases.
4. Replace the config `[VERIFY]` comments on `DiscoveryListenPort` /
   `DiscoveryBroadcastPort` with the observed port.
5. Update the release notes (16.20): the FlexRadio layout is no longer among the unverified
   areas, though RemoteRig RRC compatibility (10.16–10.18) remains so.
6. Update the status line at the top of this file.

## Privacy note

A discovery payload identifies a specific radio and carries a private-network address. It
contains no credentials. If the serial number or LAN addressing is sensitive, coordinate
before checking it in — but do not redact bytes inside the `.bin`, because that destroys the
layout evidence the fixture exists to provide.
