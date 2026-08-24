#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages the RWK v2.0 release archive.

.DESCRIPTION
    This script:
      1. Publishes RWK.Client and RWK.Station as self-contained single-file executables
      2. Builds the Go Tailscale sidecar
      3. Assembles a flat zip archive containing all deliverables

    The output is: artifacts/release/RWK-v2.0.0-win-x64.zip

.PARAMETER Version
    Semantic version for the release archive filename. Default: 2.0.0

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Version 2.1.0
#>
param(
    [string]$Version = "2.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$RepoRoot       = (Resolve-Path "$PSScriptRoot\..\..").Path
$SolutionFile   = Join-Path $RepoRoot "RWK.sln"
$ClientProject  = Join-Path $RepoRoot "src\RWK.Client\RWK.Client.csproj"
$StationProject = Join-Path $RepoRoot "src\RWK.Station\RWK.Station.csproj"
$SidecarDir     = Join-Path $RepoRoot "src\RWK.TailscaleSidecar"
$ReadmeSource   = Join-Path $PSScriptRoot "README.md"

$ArtifactsDir   = Join-Path $RepoRoot "artifacts\release"
$StagingDir     = Join-Path $ArtifactsDir "staging"
$ZipFileName    = "RWK-v$Version-win-x64.zip"
$ZipPath        = Join-Path $ArtifactsDir $ZipFileName

# Expected output names (from AssemblyName in .csproj files)
$ClientExeName  = "RWKClient.exe"
$StationExeName = "RWKStation.exe"
$SidecarExeName = "rwk-tailscale-sidecar.exe"

# Go toolchain — default to PATH, fall back to known location
$GoExe = if (Get-Command go -ErrorAction SilentlyContinue) {
    (Get-Command go).Source
} elseif (Test-Path "E:\go\bin\go.exe") {
    "E:\go\bin\go.exe"
} else {
    $null
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) {
    Write-Host "`n:: $msg" -ForegroundColor Cyan
}

function Assert-SingleFile([string]$publishDir, [string]$expectedExe) {
    $files = @(Get-ChildItem -Path $publishDir -File | Where-Object { $_.Extension -notin '.pdb','.xml' })
    if ($files.Count -eq 0) {
        throw "Publish directory '$publishDir' contains no files."
    }
    if ($files.Count -gt 1) {
        $names = ($files | ForEach-Object { $_.Name }) -join ", "
        throw "Publish directory '$publishDir' contains more than the single executable (found: $names). Single-file publish may have failed."
    }
    if ($files[0].Name -ne $expectedExe) {
        throw "Expected '$expectedExe' but found '$($files[0].Name)' in '$publishDir'."
    }
}

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
Write-Step "Pre-flight checks"

if (-not (Test-Path $SolutionFile)) {
    throw "Solution file not found: $SolutionFile"
}
if (-not (Test-Path $ClientProject)) {
    throw "Client project not found: $ClientProject"
}
if (-not (Test-Path $StationProject)) {
    throw "Station project not found: $StationProject"
}
if (-not (Test-Path $ReadmeSource)) {
    throw "Release README not found: $ReadmeSource"
}
if (-not (Test-Path $SidecarDir)) {
    throw "Sidecar source directory not found: $SidecarDir"
}
if (-not $GoExe) {
    throw "Go toolchain not found. Install Go 1.26.5+ and ensure 'go' is in PATH or available at E:\go\bin\go.exe."
}

Write-Host "  Solution : $SolutionFile"
Write-Host "  Client   : $ClientProject"
Write-Host "  Station  : $StationProject"
Write-Host "  Sidecar  : $SidecarDir"
Write-Host "  Go       : $GoExe"
Write-Host "  README   : $ReadmeSource"
Write-Host "  Output   : $ZipPath"

# ---------------------------------------------------------------------------
# Clean staging
# ---------------------------------------------------------------------------
Write-Step "Cleaning staging directory"

if (Test-Path $StagingDir) {
    Remove-Item -Recurse -Force $StagingDir
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

if (Test-Path $ZipPath) {
    Remove-Item -Force $ZipPath
}

# ---------------------------------------------------------------------------
# Step 1: Publish RWK.Client (Task 31.1)
# Requirements: 16.1, 16.3
# ---------------------------------------------------------------------------
Write-Step "Publishing RWK.Client (self-contained single-file, win-x64)"

$ClientPublishDir = Join-Path $ArtifactsDir "publish-client"
if (Test-Path $ClientPublishDir) { Remove-Item -Recurse -Force $ClientPublishDir }

& dotnet publish $ClientProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $ClientPublishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for RWK.Client (exit code $LASTEXITCODE)." }

Assert-SingleFile $ClientPublishDir $ClientExeName
Copy-Item (Join-Path $ClientPublishDir $ClientExeName) (Join-Path $StagingDir $ClientExeName)
Write-Host "  -> $ClientExeName staged"

# Copy the Wizard catalog (content file, placed alongside the exe by dotnet publish)
$ClientWizardDir = Join-Path $ClientPublishDir "Wizard"
if (Test-Path $ClientWizardDir) {
    $StagingWizardDir = Join-Path $StagingDir "Wizard"
    if (-not (Test-Path $StagingWizardDir)) {
        New-Item -ItemType Directory -Path $StagingWizardDir -Force | Out-Null
    }
    Copy-Item (Join-Path $ClientWizardDir "radios.json") (Join-Path $StagingWizardDir "radios.json") -Force
    Write-Host "  -> Wizard/radios.json staged"
}

# ---------------------------------------------------------------------------
# Step 2: Publish RWK.Station (Task 31.1)
# Requirements: 16.1, 16.3
# ---------------------------------------------------------------------------
Write-Step "Publishing RWK.Station (self-contained single-file, win-x64)"

$StationPublishDir = Join-Path $ArtifactsDir "publish-station"
if (Test-Path $StationPublishDir) { Remove-Item -Recurse -Force $StationPublishDir }

& dotnet publish $StationProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $StationPublishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for RWK.Station (exit code $LASTEXITCODE)." }

Assert-SingleFile $StationPublishDir $StationExeName
Copy-Item (Join-Path $StationPublishDir $StationExeName) (Join-Path $StagingDir $StationExeName)
Write-Host "  -> $StationExeName staged"

# ---------------------------------------------------------------------------
# Step 3: Build Go sidecar (Task 31.2)
# Requirements: 16.2, 16.6
# ---------------------------------------------------------------------------
Write-Step "Building Tailscale sidecar (Go)"

# Verify Go version meets minimum
$goVersionOutput = & $GoExe version 2>&1
Write-Host "  Go version: $goVersionOutput"

$env:GOOS = "windows"
$env:GOARCH = "amd64"
$env:CGO_ENABLED = "0"

$SidecarOutputPath = Join-Path $StagingDir $SidecarExeName

# Note: go build is run from the sidecar source directory
Push-Location $SidecarDir
try {
    & $GoExe build -o $SidecarOutputPath -ldflags "-s -w" .
    if ($LASTEXITCODE -ne 0) { throw "go build failed for Tailscale sidecar (exit code $LASTEXITCODE)." }
} finally {
    Pop-Location
}

if (-not (Test-Path $SidecarOutputPath)) {
    throw "Sidecar build produced no output at: $SidecarOutputPath"
}
Write-Host "  -> $SidecarExeName staged"

# ---------------------------------------------------------------------------
# Step 3.5: Code Signing (Task 31.3)
# TODO: Code signing is not yet implemented. A signing certificate is required.
#
# When a certificate is available, sign all three executables here BEFORE
# zipping. Per Requirement 16.13, each executable must carry a signature at
# the moment of packaging.
#
# Example (signtool from Windows SDK):
#   $signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.xxxxx.0\x64\signtool.exe"
#   $certThumbprint = "YOUR_CERT_THUMBPRINT"
#   $timestampUrl   = "http://timestamp.digicert.com"
#
#   foreach ($exe in @($ClientExeName, $StationExeName, $SidecarExeName)) {
#       $path = Join-Path $StagingDir $exe
#       & $signtool sign /sha1 $certThumbprint /tr $timestampUrl /td sha256 /fd sha256 $path
#       if ($LASTEXITCODE -ne 0) { throw "Code signing failed for $exe" }
#   }
#
# NOTE: Unsigned builds will trigger Windows SmartScreen warnings for end
# users (Requirement 16.14). This is a known limitation until signing is set up.
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Step 4: Copy README (Task 32.1)
# Requirements: 16.15-16.20
# ---------------------------------------------------------------------------
Write-Step "Copying release README"

Copy-Item $ReadmeSource (Join-Path $StagingDir "README.md")
Write-Host "  -> README.md staged"

# ---------------------------------------------------------------------------
# Step 5: Assemble zip archive (Task 31.4)
# Requirements: 16.1, 16.2, 16.4, 16.6
# ---------------------------------------------------------------------------
Write-Step "Assembling release archive: $ZipFileName"

# Verify staging contains exactly the expected four files
$stagedFiles = Get-ChildItem -Path $StagingDir -File | Sort-Object Name
$expectedFiles = @("README.md", "splash.png", $ClientExeName, $StationExeName, $SidecarExeName) | Sort-Object

$stagedNames = ($stagedFiles | ForEach-Object { $_.Name }) | Sort-Object
$missingFiles = $expectedFiles | Where-Object { $_ -notin $stagedNames }
$extraFiles   = $stagedNames | Where-Object { $_ -notin $expectedFiles }

if ($missingFiles) {
    throw "Missing files in staging: $($missingFiles -join ', ')"
}
if ($extraFiles) {
    throw "Unexpected files in staging: $($extraFiles -join ', '). Archive must contain exactly four entries."
}

# Create zip with flat structure (no nested directory)
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }

$zip = [System.IO.Compression.ZipFile]::Open($ZipPath, 'Create')
try {
    foreach ($file in $stagedFiles) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip,
            $file.FullName,
            $file.Name,                    # flat — no directory prefix
            [System.IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
} finally {
    $zip.Dispose()
}

if (-not (Test-Path $ZipPath)) {
    throw "Failed to create zip archive at: $ZipPath"
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Step "Release build complete"

$zipInfo = Get-Item $ZipPath
Write-Host ""
Write-Host "  Archive : $ZipPath"
Write-Host "  Size    : $([math]::Round($zipInfo.Length / 1MB, 2)) MB"
Write-Host ""
Write-Host "  Contents:"
foreach ($file in $stagedFiles) {
    $sizeMB = [math]::Round($file.Length / 1MB, 2)
    Write-Host "    $($file.Name.PadRight(30)) $sizeMB MB"
}
Write-Host ""
Write-Host "  NOTE: Executables are UNSIGNED. SmartScreen warnings will appear." -ForegroundColor Yellow
Write-Host "        See Task 31.3 TODO in this script for signing instructions." -ForegroundColor Yellow
Write-Host ""
