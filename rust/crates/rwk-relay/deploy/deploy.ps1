# Ships the rust/ workspace to the VPS and builds + runs the relay with Docker there.
# Run from the `rust/` directory AFTER install-key.ps1 has installed the deploy key.
#
# Usage (from rust/):
#   .\crates\rwk-relay\deploy\deploy.ps1 -VpsHost 203.0.113.10 -VpsUser ubuntu `
#       -AdminPassword 'strong-pw' -JwtSecret (python -c "import secrets;print(secrets.token_hex(32))")
#
# If -JwtSecret is omitted a strong one is generated and printed (save it — it must stay
# stable across restarts or dashboard sessions are invalidated).

param(
    [Parameter(Mandatory = $true)] [string] $VpsHost,
    [Parameter(Mandatory = $true)] [string] $VpsUser,
    [int] $Port = 22,
    [Parameter(Mandatory = $true)] [string] $AdminPassword,
    [string] $JwtSecret,
    [string] $RemoteDir = "~/rwk-relay-src"
)

$ErrorActionPreference = "Stop"
$key = "$env:USERPROFILE\.ssh\rwk_relay_deploy"
$ssh = @("-i", $key, "-p", "$Port", "-o", "IdentitiesOnly=yes")

if (-not $JwtSecret) {
    # 32 random bytes as hex — a strong signing secret.
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $JwtSecret = ($bytes | ForEach-Object { $_.ToString("x2") }) -join ""
    Write-Host "Generated RWK_JWT_SECRET (SAVE THIS): $JwtSecret" -ForegroundColor Yellow
}

# We must run this from the workspace root so the relative paths line up.
if (-not (Test-Path ".\Cargo.toml") -or -not (Test-Path ".\crates\rwk-relay")) {
    throw "run this from the rust/ workspace root (where Cargo.toml and crates/rwk-relay live)"
}

Write-Host "1/4  Preparing remote directory $RemoteDir" -ForegroundColor Cyan
ssh @ssh "${VpsUser}@${VpsHost}" "rm -rf $RemoteDir && mkdir -p $RemoteDir"

Write-Host "2/4  Copying source (this skips target/ and node_modules via a tar filter)" -ForegroundColor Cyan
# tar the workspace locally excluding build artifacts, stream it over ssh, untar on the VPS.
# This is far faster and tidier than scp -r of the whole tree with artifacts.
$tarArgs = @(
    "--exclude=target", "--exclude=*/target", "--exclude=node_modules",
    "--exclude=*/node_modules", "--exclude=dist", "--exclude=*/dist",
    "--exclude=.git", "-czf", "-", "."
)
# `tar` ships with Windows 10+; pipe its stdout straight into ssh.
& tar @tarArgs | ssh @ssh "${VpsUser}@${VpsHost}" "tar -xzf - -C $RemoteDir"

Write-Host "3/4  Building the Docker image on the VPS (this can take several minutes)" -ForegroundColor Cyan
ssh @ssh "${VpsUser}@${VpsHost}" "cd $RemoteDir && docker build -f crates/rwk-relay/Dockerfile -t rwk-relay:latest ."

Write-Host "4/4  Starting the relay container" -ForegroundColor Cyan
# Write an env file on the VPS (chmod 600) rather than putting secrets on the command line,
# so they don't show up in the shell history or `ps`.
$envFile = @"
RWK_ADMIN_PASSWORD=$AdminPassword
RWK_JWT_SECRET=$JwtSecret
"@
$envFile | ssh @ssh "${VpsUser}@${VpsHost}" "umask 077 && cat > $RemoteDir/relay.env"

ssh @ssh "${VpsUser}@${VpsHost}" @"
docker rm -f rwk-relay 2>/dev/null || true
docker run -d --name rwk-relay --restart unless-stopped \
  -p 9000:9000 -p 8080:8080 \
  --env-file $RemoteDir/relay.env \
  rwk-relay:latest
sleep 3
echo '--- container status ---'
docker ps --filter name=rwk-relay --format '{{.Status}}'
echo '--- health check ---'
docker exec rwk-relay wget -qO- http://127.0.0.1:8080/api/health || echo 'health check pending'
"@

Write-Host "`nDone. Dashboard: http://${VpsHost}:8080  (front with Cloudflare for TLS)" -ForegroundColor Green
Write-Host "Relay protocol: ${VpsHost}:9000" -ForegroundColor Green
