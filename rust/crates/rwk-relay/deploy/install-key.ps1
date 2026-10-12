# Installs the RWK relay deploy public key onto the VPS, so subsequent connections use the
# dedicated key (no password). Run this ONCE from your Windows machine.
#
# It authenticates with whatever you already have (password or an existing key) just long
# enough to append the public key to the VPS's authorized_keys. The private key never leaves
# this machine.
#
# Usage:
#   .\install-key.ps1 -VpsHost 203.0.113.10 -VpsUser ubuntu            # password prompt
#   .\install-key.ps1 -VpsHost 203.0.113.10 -VpsUser ubuntu -Port 2222

param(
    [Parameter(Mandatory = $true)] [string] $VpsHost,
    [Parameter(Mandatory = $true)] [string] $VpsUser,
    [int] $Port = 22
)

$ErrorActionPreference = "Stop"
$pub = "$env:USERPROFILE\.ssh\rwk_relay_deploy.pub"
if (-not (Test-Path $pub)) {
    throw "public key not found at $pub — generate it first with ssh-keygen"
}
$pubKey = (Get-Content $pub -Raw).Trim()
Write-Host "Installing deploy key on ${VpsUser}@${VpsHost}:${Port}" -ForegroundColor Cyan
Write-Host "You'll be prompted for your existing VPS password (or it'll use an existing key)." -ForegroundColor Yellow

# Append the key to authorized_keys on the VPS, creating ~/.ssh with correct perms if needed.
# A single here-string is piped to the remote shell so no temp file is left on the VPS.
$remote = @"
set -e
mkdir -p ~/.ssh
chmod 700 ~/.ssh
touch ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys
# Only add the key if it's not already present (idempotent).
grep -qxF '$pubKey' ~/.ssh/authorized_keys || echo '$pubKey' >> ~/.ssh/authorized_keys
echo 'deploy key installed'
"@

$remote | ssh -p $Port "${VpsUser}@${VpsHost}" "bash -s"

Write-Host "`nTesting the new key..." -ForegroundColor Cyan
ssh -i "$env:USERPROFILE\.ssh\rwk_relay_deploy" -p $Port -o IdentitiesOnly=yes "${VpsUser}@${VpsHost}" "echo 'deploy key works; docker: ' && (docker --version 2>/dev/null || echo 'docker not found')"
