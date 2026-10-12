# RWK Relay Server

The rendezvous/relay box for RWK Router Keyer. Run **one** of these on a host with a public
IP, and a Client and a Station behind any NAT can find each other and talk — with **no network
setup on the operator's end** (no port forwarding, no static IP). When a direct peer-to-peer
path can be opened the relay steps out of the data path; when it can't (both ends behind hard
NAT/CGNAT) it relays the already-encrypted traffic.

It is the same relay the RWK app embeds (`rwk-core::engine::relay`), wrapped with operator
limits, a time-series metrics layer, and a dark-themed dashboard — all in one small process /
container. The relay **never sees your traffic in the clear**: payloads are WireGuard
transport messages it only forwards, keyed by public key.

## What it exposes

| Port | Purpose | Exposure |
|---|---|---|
| `9000/tcp` | relay/rendezvous protocol — RWK nodes connect here | must be reachable from the internet |
| `8080/tcp` | dashboard + JSON API | **put Cloudflare (or another TLS proxy) in front** — the login must not travel in the clear |

## Quick start (Docker)

```sh
cd rust
export RWK_ADMIN_PASSWORD='choose-a-strong-password'
export RWK_JWT_SECRET="$(openssl rand -hex 32)"
docker compose -f crates/rwk-relay/docker-compose.yml up -d --build
```

The dashboard is then on `http://<host>:8080` (behind Cloudflare, `https://relay.example.com`).
Nodes point at `<host>:9000`.

## Configuration (environment variables)

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `RWK_ADMIN_PASSWORD` | **yes** | — | dashboard password (≥ 8 chars) |
| `RWK_JWT_SECRET` | **yes** | — | signs session tokens (≥ 16 chars; keep stable across restarts) |
| `RWK_ADMIN_USER` | no | `admin` | dashboard username |
| `RWK_RELAY_ADDR` | no | `0.0.0.0:9000` | relay protocol bind |
| `RWK_HTTP_ADDR` | no | `0.0.0.0:8080` | dashboard/API bind |
| `RWK_MAX_REGISTRATIONS` | no | `2000` | cap on concurrently registered nodes |
| `RWK_PER_CONN_BYTES_PER_SEC` | no | `500000` | per-connection relay rate cap (~4 Mbit/s; `0` disables) — protects your transfer allowance |
| `RWK_SESSION_SECS` | no | `86400` | dashboard session lifetime |

The relay refuses to start without `RWK_ADMIN_PASSWORD` and `RWK_JWT_SECRET`, so it is never
exposed with a guessable login or a predictable token signer.

## TLS via Cloudflare

The container serves the dashboard as plain HTTP on `8080`; Cloudflare terminates TLS at the
edge, so there is no cert to manage on the box:

1. Point a DNS record (e.g. `relay.example.com`) at the VPS, **proxied** (orange cloud).
2. In Cloudflare, set SSL/TLS mode to **Full** and add an origin rule / tunnel forwarding
   `:443` → origin `:8080` (or run `cloudflared` on the box pointing at `127.0.0.1:8080`).
3. Keep `8080` closed to the public internet at the firewall; let only Cloudflare reach it
   (or expose it solely via a Cloudflare Tunnel, which needs no inbound port at all).

The relay port `9000` is a raw TCP protocol (not HTTP), so it does **not** go through
Cloudflare — open it directly in the VPS firewall.

## Capacity

On a 1 GB / 2 TB-transfer VPS: comfortably ~1,000–2,000 nodes registered at once, and — because
pairs that punch a direct path cost the relay almost nothing — a few hundred actively-relayed
pairs at normal ham duty cycles (CW + control + Opus). Only pairs that *both* fall back to the
relay *and* push heavy forwards (FlexRadio audio) consume the transfer budget; the per-connection
rate cap keeps any single pair from eating it.

## Metrics dashboard

Dark theme, live charts (relay throughput, active nodes) and health counters (frames relayed,
drops by cause, rejections), polling every 2 s. Single admin login; the session is an
`HttpOnly` cookie.

## Self-hosting

The published image on GitHub lets anyone run their own relay instead of using a shared one;
the RWK app's settings let a user point at a different relay address. Pull and run:

```sh
docker run -d --name rwk-relay -p 9000:9000 -p 8080:8080 \
  -e RWK_ADMIN_PASSWORD='...' -e RWK_JWT_SECRET='...' \
  ghcr.io/w1ve/rwk-relay:latest
```
