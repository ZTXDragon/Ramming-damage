# Builds a release-ready Workshop package for ZTX Ramming Damage.
# Run from RammingDamage-Source/. Output goes to ../dist/ztx.ramming_damage/
# (sibling of RammingDamage-Source/, inside the mod project folder).
#
# Existing dist/ contents are wiped at the start of each run, so every
# rebuild produces a fresh, identical-layout output. Override with
# -OutputDir if you need a different destination.
#
# NOTE on duplicate mod registration: because dist/ lives under Mods/,
# Cosmoteer's scanner can pick it up as a duplicate of the installed
# ztx.ramming_damage mod. To handle that at publish time, either move
# the bundle out of Mods/ before launching the game, or use the
# Workshop publish dialog directly on the installed dev copy at
# Mods/ztx.ramming_damage/ (which is byte-identical to dist/).
#
# Bundle layout:
#   ../dist/ztx.ramming_damage/
#     ZTX.RammingDamage.dll
#     mod.rules
#     config.rules                   (player-tunable, DebugLog=false)
#     README.md                      (player-facing docs)
#     source/                        (full C# source for transparency)
#       *.cs
#       ZTX.RammingDamage.csproj
#       build_and_install.ps1
#       README.md                    (source-bundle docs)
#       config.rules.template
#
# Cosmoteer must be CLOSED during build (Windows locks the running DLL).

[CmdletBinding()]
param(
    [string]$CosmoteerBin = $env:COSMOTEER_BIN,
    [string]$YamlModDir   = $env:YAML_MOD_DIR,
    [string]$OutputDir    = ''
)

if (-not $CosmoteerBin) { $CosmoteerBin = 'D:\SteamLibrary\steamapps\common\Cosmoteer\Bin' }
if (-not $YamlModDir)   { $YamlModDir   = 'D:\SteamLibrary\steamapps\workshop\content\799600\3577650065' }

$ErrorActionPreference = 'Stop'
$script:here = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $script:here

if (-not $OutputDir) {
    # Default: dist/ at the top of the project workspace (parent of RammingDamage-Source/).
    # Wiped + rewritten on every run so layout stays identical to the
    # latest build.
    $projectRoot = Split-Path -Parent $script:here
    $OutputDir = Join-Path $projectRoot 'dist\ztx.ramming_damage'
}

Write-Host "=== ZTX Ramming Damage -- Workshop package ===" -ForegroundColor Cyan
Write-Host "CosmoteerBin: $CosmoteerBin"
Write-Host "YamlModDir:   $YamlModDir"
Write-Host "OutputDir:    $OutputDir"
Write-Host ''

foreach ($p in @(
    (Join-Path $CosmoteerBin 'Cosmoteer.dll'),
    (Join-Path $CosmoteerBin 'HalflingCore.dll'),
    (Join-Path $YamlModDir   '0Harmony.dll')
)) {
    if (-not (Test-Path $p)) {
        throw "Required reference not found: $p (override -CosmoteerBin / -YamlModDir)"
    }
}

# Clean previous output.
if (Test-Path $OutputDir) {
    Write-Host "Removing previous output at $OutputDir..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# Build.
Write-Host 'Building (dotnet build -c Release)...' -ForegroundColor Yellow
& dotnet build `
    -c Release `
    -p:CosmoteerBin="$CosmoteerBin" `
    -p:YamlModDir="$YamlModDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $script:here 'bin\Release\net10.0\ZTX.RammingDamage.dll'
if (-not (Test-Path $dll)) { throw "Build succeeded but output DLL not found at: $dll" }

# Install-folder layout.
Write-Host 'Copying install files...' -ForegroundColor Yellow
Copy-Item -Force $dll                                           (Join-Path $OutputDir 'ZTX.RammingDamage.dll')
Copy-Item -Force (Join-Path $script:here 'mod.rules.template')  (Join-Path $OutputDir 'mod.rules')
Copy-Item -Force (Join-Path $script:here 'README.md')           (Join-Path $OutputDir 'README.md')
Copy-Item -Force (Join-Path $script:here 'LICENSE')             (Join-Path $OutputDir 'LICENSE')

# Shipped config.rules: copy from template (which has DebugLog=false default).
$configSrc = Join-Path $script:here 'config.rules.template'
$configDst = Join-Path $OutputDir   'config.rules'
Copy-Item -Force $configSrc $configDst
# Defensive: force DebugLog=false in the shipped copy in case the template
# was edited locally with a true value.
(Get-Content $configDst) `
    -replace '(?im)^(\s*DebugLog\s*=\s*)(true|1|yes|on)\b', '$1false' `
    | Set-Content $configDst

# NOTE: the mod no longer ships a source bundle -- source is published to
# GitHub instead. Only the runtime files above are packaged.

# Sanity check: walk the output and report the final tree.
Write-Host ''
Write-Host 'Output:' -ForegroundColor Cyan
Get-ChildItem -Recurse $OutputDir | ForEach-Object {
    $rel = $_.FullName.Substring($OutputDir.Length).TrimStart('\')
    if ($_.PSIsContainer) {
        Write-Host "  [dir]  $rel"
    } else {
        $kb = [Math]::Round($_.Length / 1KB, 1)
        Write-Host ("  {0,7} KB  {1}" -f $kb, $rel)
    }
}

Write-Host ''
Write-Host "Done." -ForegroundColor Green
Write-Host "Workshop-ready folder: $OutputDir" -ForegroundColor Cyan
Write-Host ''
Write-Host "To publish:"
Write-Host "  1. Copy/move the ztx.ramming_damage folder to your Cosmoteer Mods directory."
Write-Host "  2. In the in-game Mods menu, use the Publish-to-Workshop action on the mod."
Write-Host "     (or copy it to your Workshop-uploader staging folder for a manual upload)"
