# Builds ZTX.RammingDamage.dll and installs the FULL shipped layout into
# the user's Cosmoteer Mods folder so YAML can find it on next launch.
# Run from RammingDamage-Source/. The install folder mirrors what end users see in
# the Workshop release:
#
#   ztx.ramming_damage/
#     ZTX.RammingDamage.dll
#     mod.rules
#     config.rules                   (preserved across rebuilds; not clobbered)
#     README.md
#     source/
#       *.cs
#       ZTX.RammingDamage.csproj
#       build_and_install.ps1
#       README.md
#       config.rules.template
#       mod.rules.template
#
# This is intentionally identical to what package_for_workshop.ps1 produces,
# minus the dist/ wrapper -- so the dev install always reflects the user-
# facing layout.
#
# Cosmoteer must be CLOSED during build (Windows locks the running DLL).

[CmdletBinding()]
param(
    [string]$CosmoteerBin  = $env:COSMOTEER_BIN,
    [string]$YamlModDir    = $env:YAML_MOD_DIR,
    [string]$ModInstallDir = ''
)

if (-not $CosmoteerBin) { $CosmoteerBin = 'D:\SteamLibrary\steamapps\common\Cosmoteer\Bin' }
if (-not $YamlModDir)   { $YamlModDir   = 'D:\SteamLibrary\steamapps\workshop\content\799600\3577650065' }

$ErrorActionPreference = 'Stop'
$script:here = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $script:here

if (-not $ModInstallDir) {
    $savedGamesRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Saved Games\Cosmoteer'
    $pickedUserDir = $null
    if (Test-Path $savedGamesRoot) {
        $pickedUserDir = Get-ChildItem -Path $savedGamesRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+$' } |
            Select-Object -First 1
    }
    if ($pickedUserDir) {
        $ModInstallDir = Join-Path $pickedUserDir.FullName 'Mods\ztx.ramming_damage'
    } else {
        $cosmoRoot = Split-Path -Parent $CosmoteerBin
        $ModInstallDir = Join-Path $cosmoRoot 'Mods\ztx.ramming_damage'
        Write-Warning "No Saved Games profile found; falling back to game-install Mods dir."
    }
}

Write-Host "=== ZTX Ramming Damage build + install ===" -ForegroundColor Cyan
Write-Host "CosmoteerBin:   $CosmoteerBin"
Write-Host "YamlModDir:     $YamlModDir"
Write-Host "ModInstallDir:  $ModInstallDir"
Write-Host ''

foreach ($p in @(
    (Join-Path $CosmoteerBin 'Cosmoteer.dll'),
    (Join-Path $CosmoteerBin 'HalflingCore.dll'),
    (Join-Path $YamlModDir   '0Harmony.dll')
)) {
    if (-not (Test-Path $p)) {
        throw "Required file not found: $p (adjust -CosmoteerBin / -YamlModDir)"
    }
}

# Build.
# Guard: Log.cs VERSION and mod.rules.template Version must agree, or every log
# line a user pastes into a bug report names the wrong build.
$logVer = (Select-String -Path (Join-Path $script:here 'Log.cs') `
    -Pattern 'VERSION\s*=\s*"v([0-9.]+)"').Matches[0].Groups[1].Value
$modVer = (Select-String -Path (Join-Path $script:here 'mod.rules.template') `
    -Pattern '^\s*Version\s*=\s*([0-9.]+)').Matches[0].Groups[1].Value
if ($logVer -ne $modVer) {
    throw "Version mismatch: Log.cs says v$logVer, mod.rules.template says $modVer. Sync them."
}
Write-Host "Version check OK: $modVer" -ForegroundColor DarkGray

Write-Host 'Building (dotnet build -c Release)...' -ForegroundColor Yellow
& dotnet build `
    -c Release `
    -p:CosmoteerBin="$CosmoteerBin" `
    -p:YamlModDir="$YamlModDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

# Directory.Build.props redirects BaseOutputPath to %LOCALAPPDATA% so build
# artifacts never land inside the Mods folder (Cosmoteer scans it recursively
# for DLLs). Look for the DLL where the redirect actually puts it, with the
# pre-redirect location as a fallback for older trees.
$dll = Join-Path $env:LOCALAPPDATA 'ZTX.RammingDamage.Build\bin\Release\net10.0\ZTX.RammingDamage.dll'
if (-not (Test-Path $dll)) {
    $legacyDll = Join-Path $script:here 'bin\Release\net10.0\ZTX.RammingDamage.dll'
    if (Test-Path $legacyDll) {
        $dll = $legacyDll
    } else {
        throw "Build succeeded but output DLL not found at: $dll (nor at $legacyDll)"
    }
}

# Ensure install dir + source subdir exist.
if (-not (Test-Path $ModInstallDir)) {
    New-Item -ItemType Directory -Path $ModInstallDir -Force | Out-Null
}
# NOTE (2026-08-19): the mod no longer ships a source bundle. Source lives in
# the dev workspace and is published to GitHub instead. This also removes the
# old self-copy hazard -- when this script was run from inside the install's
# source/ folder, $script:here and $sourceDir resolved to the same path and the
# bundle copy overwrote files with themselves.

# Refresh non-locked files first (mod.rules, README) so they update even if
# Cosmoteer is running and the DLL copy fails. The DLL copy is deferred to the
# end so a partial-success run still updates everything else.
Write-Host 'Copying install files...' -ForegroundColor Yellow

Copy-Item -Force (Join-Path $script:here 'mod.rules.template') (Join-Path $ModInstallDir 'mod.rules')
Copy-Item -Force (Join-Path $script:here 'README.md')          (Join-Path $ModInstallDir 'README.md')
Copy-Item -Force (Join-Path $script:here 'LICENSE')            (Join-Path $ModInstallDir 'LICENSE')

# Install config.rules from template only if it doesn't exist.
# Preserves user edits on subsequent builds.
$configSrc = Join-Path $script:here 'config.rules.template'
$configDst = Join-Path $ModInstallDir 'config.rules'
if (-not (Test-Path $configDst)) {
    Copy-Item -Force $configSrc $configDst
    Write-Host "Installed default config.rules (existing config preserved on later builds)."
}

# DLL copy LAST so a Cosmoteer-locked DLL doesn't abort the rest.
Write-Host 'Copying DLL...' -ForegroundColor Yellow
$dllDest = Join-Path $ModInstallDir 'ZTX.RammingDamage.dll'
try {
    Copy-Item -Force $dll $dllDest
}
catch {
    Write-Warning "DLL copy failed (is Cosmoteer running? Close it and rerun)."
    Write-Warning "  $_"
    Write-Warning "Source bundle and config files DID update -- only the DLL was skipped."
}

Write-Host ''
Write-Host "Done." -ForegroundColor Green
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  1. Launch Cosmoteer."
Write-Host "  2. Mods menu -> find 'Ramming Damage' -> enable. Trust the DLL when YAML asks."
Write-Host "  3. Restart the game. Look for '[ZTX.Ramming]' lines in"
Write-Host "     <Saved Games>\Cosmoteer\<id>\Logs\log *.txt"
