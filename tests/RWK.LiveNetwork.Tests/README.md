# RWK.LiveNetwork.Tests

Live tailnet integration tests that exercise the Go sidecar over a real Tailscale
network. These verify what no unit test can: datagrams actually crossing the mesh,
Direct path establishment, RTT reporting, and TCP tunnel forwarding.

## Prerequisites

1. **Go sidecar built** — the binary must exist at
   `src/RWK.TailscaleSidecar/rwk-tailscale-sidecar.exe` relative to the
   solution root. Build it with:
   ```powershell
   cd src/RWK.TailscaleSidecar
   go build -o rwk-tailscale-sidecar.exe .
   ```

2. **Tailscale pre-auth key** — set the environment variable `RWK_TEST_AUTHKEY`
   to a reusable, ephemeral pre-auth key from your Tailscale admin console.
   The key should be tagged (e.g., `tag:rwk-station`, `tag:rwk-client`) so
   the test nodes are identifiable and can be ACL-scoped.

3. **Same tailnet** — both sidecar instances join the tailnet associated with
   the auth key. They need to be able to reach each other directly (same LAN
   or open UDP path) for the Direct path tests to pass quickly.

## Running

These tests are **not** part of the default `dotnet test` run. Run them
explicitly:

```powershell
dotnet test tests/RWK.LiveNetwork.Tests/ --filter Category=LiveNetwork
```

Or from the solution root:

```powershell
dotnet test --filter Category=LiveNetwork
```

## Skip behavior

When `RWK_TEST_AUTHKEY` is not set or the sidecar binary is not found, all tests
skip gracefully with a descriptive message. CI without a key still passes.

## What the harness does

1. Checks for `RWK_TEST_AUTHKEY`; skips if absent.
2. Locates `rwk-tailscale-sidecar.exe` relative to the solution root.
3. Launches sidecar A ("rwk-test-station") and sidecar B ("rwk-test-client")
   with unique state directories in the system temp folder.
4. `POST /v1/start` to both with the auth key.
5. Polls `/v1/status` until both report `state=Connected`.
6. Sets each as the other's peer via `POST /v1/peer`.
7. Waits for `path=Direct` on both (up to 60 seconds).
8. On teardown: `POST /v1/stop`, close stdin, wait for exit, kill if necessary,
   delete state directories.

## Tailscale ACL considerations

If your tailnet uses ACLs, ensure the tags used by the auth key can reach each
other on all ports. A minimal ACL entry:

```json
{
  "action": "accept",
  "src": ["tag:rwk-station", "tag:rwk-client"],
  "dst": ["tag:rwk-station:*", "tag:rwk-client:*"]
}
```

## Timeouts

- Handshake: 15 seconds (sidecar must emit its JSON line)
- Connect: 90 seconds (tailnet join)
- Direct path: 60 seconds (NAT traversal / hole punching)
- Teardown: 5 seconds per sidecar (stop + wait for exit)
