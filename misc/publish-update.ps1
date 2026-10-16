<#
.SYNOPSIS
  Build the NekoNexus client mod and publish it to the in-game auto-update channel(s).

  Builds the transport shim + mod + bootstrap + Discord RPC, stages the DLLs into the update
  tree (server-data/updates/v2/<channel>/...), regenerates the manifest the in-game auto-updater
  reads, and (optionally) refreshes NekoNexusSetup.exe so the one-click installer ships the same
  build. With -Push it also publishes the channel tree to the `updates` branch (for GitHub Pages /
  raw hosting), which is how the GitHub Actions self-hosted-runner workflow distributes updates
  globally.

  REQUIRES the real UberStrike game reference assemblies under packages/AssemblyReferences/4.7.1/
  (Assembly-CSharp.dll etc.) - the mod cannot compile without them. Run this on a machine that has
  UberStrike installed (or a self-hosted CI runner that does); GitHub-hosted runners cannot.

.PARAMETER Channel
  'beta', 'stable', or 'all' (both). Default 'beta'.

.PARAMETER RefreshInstaller
  Also rebuild .release/installer/NekoNexusSetup.exe with the freshly built DLLs (needs Inno Setup).

.PARAMETER Push
  After publishing, force-push the channel tree + installer to the `updates` orphan branch so it's
  served by GitHub Pages / raw. Used by the CI workflow; safe to run locally too.

.EXAMPLE
  .\misc\publish-update.ps1 -Channel beta                 # push current build to beta testers
  .\misc\publish-update.ps1 -Channel all -RefreshInstaller # both channels + refresh the installer
  .\misc\publish-update.ps1 -Channel all -Push            # build, both channels, publish to repo
#>
[CmdletBinding()]
param(
  [ValidateSet('stable', 'beta', 'all')][string]$Channel = 'beta',
  [switch]$RefreshInstaller,
  [switch]$Push
)
$ErrorActionPreference = 'Stop'
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Base = Join-Path $Repo 'server-data'

$channels = if ($Channel -eq 'all') { @('beta', 'stable') } else { @($Channel) }

# Fail fast with a clear message if the game refs aren't present (the #1 cause of a failed build here).
$gameRef = Join-Path $Repo 'packages\AssemblyReferences\4.7.1\Assembly-CSharp.dll'
if (-not (Test-Path $gameRef)) {
  throw "Game reference assemblies missing ($gameRef). Copy Assembly-CSharp(.dll/-firstpass), Photon3Unity3D.dll and UnityEngine.dll from your UberStrike_Data\Managed into packages\AssemblyReferences\4.7.1\ first. The client mod cannot compile without them."
}

# 1) Build the free transport shim + the client mod (latest source).
Step 'Building transport shim + client mod'
dotnet build (Join-Path $Repo 'shims\Photon3Unity3D.Shim\Photon3Unity3D.Shim.csproj') -c Release | Out-Null
dotnet build (Join-Path $Repo 'packages\Client\Current\NekoNexus.Client.Bootstrap\NekoNexus.Client.Bootstrap.csproj') -c Release | Out-Null
dotnet build (Join-Path $Repo 'packages\Client\Current\NekoNexus.Client\NekoNexus.Client.csproj') -c Release | Out-Null
dotnet build (Join-Path $Repo 'packages\Client\Current\NekoNexus.Client.DiscordRPC\NekoNexus.Client.DiscordRPC.csproj') -c Release | Out-Null

# 2) Stage the files the client should receive, matching the in-game folder layout. The same build
#    goes to every requested channel (promote beta -> stable by publishing both).
$ModSrc = Join-Path $Repo '.release\client\_pak\UberStrike_Data\Managed'
$Shim   = Join-Path $Repo 'packages\Core\AssemblyReferences\Photon3Unity3D.dll'
$RpcExe = Join-Path $Repo '.release\client\_pak\UberStrike_Data\Plugins\NekoNexus.Client.DiscordRPC.exe'

foreach ($ch in $channels) {
  $Dest = Join-Path $Base "updates\v2\$ch\universal\UberStrike_Data\Managed"
  Step "Staging update files -> $ch"
  New-Item -ItemType Directory -Force -Path $Dest | Out-Null
  # Remove stale artifacts from a previous Paradise build so the regenerated manifest is
  # NekoNexus-only (the renamed assemblies do not overwrite the old Paradise.* filenames).
  Get-ChildItem -Path $Dest -Filter 'Paradise.*' -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    - $($_.Name) (old Paradise)"; Remove-Item $_.FullName -Force
  }
  foreach ($f in 'NekoNexus.Client.Bootstrap.dll', 'NekoNexus.Client.dll', '0Harmony.dll', 'log4net.dll', 'YamlDotNet.dll') {
    Copy-Item (Join-Path $ModSrc $f) $Dest -Force
    Write-Host "    + $f"
  }
  Copy-Item $Shim (Join-Path $Dest 'Photon3Unity3D.dll') -Force
  Write-Host '    + Photon3Unity3D.dll'

  # Discord Rich Presence helper is Windows-only -> 'win' platform tree -> UberStrike_Data\Plugins on
  # the client. The manifest marks it optional so the auto-updater keeps it fresh when present.
  if (Test-Path $RpcExe) {
    $RpcDest = Join-Path $Base "updates\v2\$ch\win\UberStrike_Data\Plugins"
    New-Item -ItemType Directory -Force -Path $RpcDest | Out-Null
    Get-ChildItem -Path $RpcDest -Filter 'Paradise.*' -File -ErrorAction SilentlyContinue | ForEach-Object {
      Write-Host "    - $($_.Name) (old Paradise)"; Remove-Item $_.FullName -Force
    }
    Copy-Item $RpcExe $RpcDest -Force
    Write-Host '    + NekoNexus.Client.DiscordRPC.exe (win)'
  }
}

# 2b) Stage the bundled maps (Space City / Spaceport Alpha / UberZone) into every channel so the
#     in-game auto-updater delivers them, and seed each channel's PREVIOUS version from the live
#     manifest so gen-updates can bump it. CI checks out a clean tree (server-data is gitignored), so
#     without these two steps every publish would drop the maps and reset the version to the base.
$MapSrc = Join-Path $Repo 'client-assets\Maps'
foreach ($ch in $channels) {
  $chDir = Join-Path $Base "updates\v2\$ch"
  New-Item -ItemType Directory -Force -Path $chDir | Out-Null

  # Seed the prior version (raw branch = updated immediately on push, unlike Pages). gen-updates reads
  # this existing updates.yml, bumps its last segment, then overwrites it.
  try {
    Invoke-WebRequest "https://raw.githubusercontent.com/nekosune-gameservers-mp-mods/NekoNexus/updates/v2/$ch/updates.yml" -OutFile (Join-Path $chDir 'updates.yml') -UseBasicParsing -TimeoutSec 20 | Out-Null
    Write-Host "    seeded prior version for $ch"
  } catch { Write-Host "    (no prior $ch manifest to seed - first publish)" }

  if (Test-Path $MapSrc) {
    $MapDest = Join-Path $Base "updates\v2\$ch\universal\UberStrike_Data\Maps"
    New-Item -ItemType Directory -Force -Path $MapDest | Out-Null
    Copy-Item (Join-Path $MapSrc '*.unity3d') $MapDest -Force
    Get-ChildItem $MapDest -Filter *.unity3d | ForEach-Object { Write-Host "    + map $($_.Name)" }
  }
}

# 3) Regenerate the manifest (hashes every file in every channel that exists under server-data).
Step 'Generating update manifest'
Push-Location (Join-Path $Repo 'packages\ws')
try { & bun src/index.ts gen-updates --dir $Base } finally { Pop-Location }

# 3b) Mirror into the web service's own wwwroot so a from-source `bun src/index.ts` run (no docker
#     volume) serves the same channel. Docker users mount server-data/updates directly, so this is
#     only for non-docker hosting - harmless either way.
$wwwroot = Join-Path $Repo 'packages\ws\wwwroot\updates'
Step 'Mirroring channel into packages/ws/wwwroot/updates'
New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
Copy-Item (Join-Path $Base 'updates\*') $wwwroot -Recurse -Force

# 4) Optionally refresh the one-click installer so it ships the same DLLs.
if ($RefreshInstaller) {
  Step 'Refreshing NekoNexusSetup.exe'
  & (Join-Path $Repo 'misc\build-installer.ps1')
}

# 5) Optionally publish the channel tree (+ installer) to the `updates` orphan branch for GitHub
#    Pages / raw hosting. This is what makes updates global without a private file server.
if ($Push) {
  Step 'Publishing channel to the `updates` branch'
  & (Join-Path $Repo 'misc\publish-update-branch.ps1')
}

Write-Host ''
Write-Host "Published channel(s): $($channels -join ', ')" -ForegroundColor Green
foreach ($ch in $channels) {
  Write-Host "  manifest: $Base\updates\v2\$ch\updates.yml"
}
Write-Host '  served at: <your-domain>/updates/v2/<channel>/updates.yml (docker server-data volume),'
Write-Host '             or https://<user>.github.io/<repo>/v2/<channel>/updates.yml (GitHub Pages, with -Push).'
Write-Host 'Players on that channel with Auto-Updates enabled get it on next launch.'
