# macOS build & test — drop-and-go plan

Everything needed to take the W1VE keyer suite onto a rented Apple-silicon Mac (M4 Mac
mini VPS or any Mac), build all the apps, test what a headless cloud box can test, and sign +
notarize distributable bundles.

> **Scope of the rental.** A headless cloud Mac can build, launch, open serial *devices*,
> decode WinKeyer, hold keying timing, and sign/notarize — roughly 90% of the macOS risk. The
> remaining 10% (a real DTR/RTS edge toggling a real key line, and USB-serial permission
> prompts) needs a physical Mac with the adapter plugged in. Plan a short hands-on session on
> real hardware to close that out; see [serial testing](#4-serial-testing-on-a-headless-mac).

> **These planning docs are dev-only.** The public `w1ve-keyer-suite` repo must not carry
> this folder — its `.gitignore` excludes `docs/mac/`.

## 0. The suite at a glance

Three repos, checked out **side by side under one parent** (the shared engine is a relative
path dependency):

```
<parent>/
  RWK-Rust/            # Keyer-Router + the shared engine (rwk-core) + the relay
  W1VE-Remote-Keyer/   # Remote Keyer (depends on ../RWK-Rust/rust/crates/rwk-core)
  W1VE-Keyer/          # standalone Keyer   (depends on ../RWK-Rust/rust/crates/rwk-core)
```

Five desktop binaries build from these:

| Binary                      | Repo / crate                         | Role |
|-----------------------------|--------------------------------------|------|
| `w1ve-rkr`                  | RWK-Rust `rwk-app`                    | Keyer-Router — operating position |
| `w1ve-rkr-station`          | RWK-Rust `rwk-app`                   | Keyer-Router — radio site |
| `w1ve-remote-keyer`         | W1VE-Remote-Keyer `wrk-app`          | Remote Keyer — operating position |
| `w1ve-remote-keyer-station` | W1VE-Remote-Keyer `wrk-app`          | Remote Keyer — radio site |
| `w1ve-keyer`                | W1VE-Keyer `wk-app`                  | standalone software WinKeyer |

(The relay, `rwk-relay`, is a Linux server container and is not part of the macOS desktop
build.)

## 1. Scripts in this folder

Run them in order on the fresh Mac. Each is idempotent — safe to re-run.

| Script | What it does |
|--------|--------------|
| [`provision.sh`](./provision.sh) | Installs Xcode CLT, Homebrew, Rust (aarch64), Node, `socat`, and clones the three repos side by side into one parent. |
| [`build-all.sh`](./build-all.sh) | Builds the web bundles and all five release binaries; prints where each landed. |
| [`serial-test.sh`](./serial-test.sh) | Creates a virtual serial pair with `socat` and feeds a WinKeyer2 byte sequence, to exercise the logger-decode path with no hardware. |
| [`sign-notarize.sh`](./sign-notarize.sh) | Signs a built `.app` with Developer ID, submits it for notarization, and staples the ticket. Reads secrets from env vars — see the script header. |

## 2. Provisioning

```sh
# On the fresh Mac, from the directory you want as <parent>:
curl -O <this-folder>/provision.sh   # or scp it up
chmod +x provision.sh
./provision.sh
```

It installs toolchains and clones the repos. If the repos are private, it expects a working
`git` credential (ssh key or a PAT in the clone URL) — set `SUITE_GIT_BASE` to your git host
org URL first (see the script header).

The one macOS-specific code gap it does **not** fix is the keying-thread priority backend —
see [§5](#5-the-one-code-gap-mach-thread-priority).

## 3. Building

```sh
./build-all.sh
```

Native `aarch64-apple-darwin` build — no cross-compile, no xwin, none of the Windows
build-env dance. The script sources nothing special; it just needs the toolchains from
`provision.sh` on `PATH`. Each app's standalone bundle needs the `custom-protocol` feature so
the webview loads the bundled `web/dist` instead of a dev URL; the script passes it.

## 4. Serial testing on a headless Mac

There is no USB bus on a VPS, so split serial testing into two layers:

**Layer 1 — virtual serial pairs (works on the VPS).** `serial-test.sh` runs
`socat -d -d pty,raw,echo=0 pty,raw,echo=0` to make two linked PTYs, then writes a WinKeyer2
host sequence into one end. Point a Keyer's **logger** port at the other end and confirm the
decode→CW path runs on real macOS device nodes. This covers port enumeration, opening a port
through the `serialport` crate on macOS (the IOKit path), and the entire WinKeyer host decode.

**Layer 2 — real control lines (needs hardware).** `write_data_terminal_ready` /
`write_request_to_send` toggle physical DTR/RTS pins. A PTY has no such pins, so the actual
key-line edge cannot be observed on a VPS. Verify this on a physical Mac with the USB-serial
adapter: plug it in, select it as the keying output, and confirm the transistor keys the rig.
This is a ~30-minute check, not a project.

Keying **timing** (the "keys within 2 ms" guarantee) is pure software and *is* testable on the
VPS — run `cargo test -p rwk-core` and the keying-over-tunnel integration test under load.

## 5. The one code gap: Mach thread priority

`rwk-core/src/platform.rs` has Windows and Linux backends for raising the keying thread to
time-critical priority; macOS currently falls through to `Unsupported` (keying runs at normal
priority). The app still works, but to hold sub-millisecond keying deadlines under load,
macOS needs a `thread_policy_set` backend using `THREAD_TIME_CONSTRAINT_POLICY`.

This is the first real coding task on the Mac. It is a self-contained addition to the
`imp` module in `platform.rs` (the one module allowed `unsafe`), gated on
`#[cfg(target_os = "macos")]`, and needs no serial hardware to validate — just the keying
timing tests under load.

## 6. Signing & notarization

Cost is $0 beyond the existing Apple Developer subscription; the work is one-time setup.

```sh
export APPLE_SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export APPLE_ID="you@example.com"
export APPLE_TEAM_ID="TEAMID"
export APPLE_APP_PASSWORD="app-specific-password"   # from appleid.apple.com
./sign-notarize.sh "path/to/W1VE Keyer.app"
```

The script codesigns with the hardened runtime, submits with `notarytool`, waits, and staples.
Entitlements for serial + microphone (sidetone) live in [`entitlements.plist`](./entitlements.plist).
Tauri can also drive signing during `tauri build`; the script is the manual path for a bundle
already built by `build-all.sh`.

## 7. Suggested month-long order

1. `provision.sh`, then `build-all.sh` — prove all five binaries build and launch (day 1).
2. Write the Mach priority backend in `platform.rs`; run keying-timing tests under load.
3. `serial-test.sh` — prove the logger decode on macOS device nodes.
4. Signing + notarization pipeline on one app, then the rest.
5. Short hands-on session on a physical Mac with the USB-serial adapter to confirm real
   key-line edges and the USB-serial permission prompt.
```
