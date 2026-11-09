<#
.SYNOPSIS
  Patches an UberStrike install to run NekoNexus on the FREE LiteNetLib server
  (no Photon SDK). Builds the NekoNexus client mod against YOUR copy of UberStrike,
  injects the bootstrap with UniversalUnityPatcher, installs our free Photon3Unity3D
  transport shim, and writes a settings file pointing at your server.

.NOTES
  Requires: the .NET SDK + the built UniversalUnityPatcher (run from a repo checkout).
  You must own a copy of UberStrike — game assemblies are never redistributed.

.EXAMPLE
  .\make-client-patch.ps1 -UberStrikePath "C:\Steam\steamapps\common\UberStrike" -ServerHost 127.0.0.1
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$UberStrikePath,
  # Defaults to the NekoNexus domain over HTTPS (one domain, no ports - see misc/reverse-proxy).
  [string]$ServerHost = 'nekonexus.nekosunevr.co.uk',
  [int]$WebPort = 8080,
  [int]$FilePort = 8081,
  # Use this when the web service is behind ONE HTTPS domain (e.g. a Cloudflare tunnel /
  # nginx proxy): the client uses https://<ServerHost>/2.0, /images, /updates (no ports).
  [switch]$Https,
  [string]$RepoRoot,
  [switch]$SkipBuild,
  [switch]$NoPatch
)

$ErrorActionPreference = "Stop"

# No -ServerHost given: the default domain is served over HTTPS behind the reverse proxy.
if (-not $PSBoundParameters.ContainsKey('ServerHost')) { $Https = $true }

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Need($p, $what) { if (-not (Test-Path $p)) { throw "$what not found: $p" } }

# Find the repo (the folder containing NekoNexus.sln) by walking up from the script and the
# current directory. Works whether the script is run from packages/Client OR the packaged
# .release/client/... copy. Override with -RepoRoot if needed.
function Find-RepoRoot([string]$start) {
  $dir = $start
  while ($dir) {
    if (Test-Path (Join-Path $dir "NekoNexus.sln")) { return $dir }
    $parent = Split-Path $dir -Parent
    if (-not $parent -or $parent -eq $dir) { return $null }
    $dir = $parent
  }
  return $null
}
if (-not $RepoRoot) { $RepoRoot = Find-RepoRoot $PSScriptRoot }
if (-not $RepoRoot) { $RepoRoot = Find-RepoRoot (Get-Location).Path }
if (-not $RepoRoot) {
  throw "Could not find the NekoNexus repo (NekoNexus.sln). Building the client mod needs a repo checkout. Pass -RepoRoot <path-to-NekoNexus>."
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
Write-Host "    repo: $RepoRoot"

$Managed = Join-Path $UberStrikePath "UberStrike_Data\Managed"
$DataDir = Join-Path $UberStrikePath "UberStrike_Data"
$RefDir  = Join-Path $RepoRoot "packages\AssemblyReferences\4.7.1"

# Prefer repo-built tools; fall back to copies sitting next to this script (the bundle).
function Pick($a, $b) { if (Test-Path $a) { return $a } else { return $b } }
$PatcherExe = Pick (Join-Path $RepoRoot "packages\Patcher\bin\Release\UniversalUnityPatcher.exe") (Join-Path $PSScriptRoot "patcher\UniversalUnityPatcher.exe")
$PatchXml   = Pick (Join-Path $RepoRoot "packages\Client\NekoNexus.Patch.xml") (Join-Path $PSScriptRoot "NekoNexus.Patch.xml")

Need $Managed "UberStrike Managed folder"
Need (Join-Path $Managed "Assembly-CSharp.dll") "Assembly-CSharp.dll (is this an UberStrike install?)"

# 1) Make YOUR game's reference assemblies available to the mod build. These come from
#    your install and are never committed/redistributed.
Step "Copying game reference assemblies"
New-Item -ItemType Directory -Force -Path $RefDir | Out-Null
foreach ($dll in @("Assembly-CSharp.dll","Assembly-CSharp-firstpass.dll","UnityEngine.dll")) {
  $src = Join-Path $Managed $dll
  Need $src $dll
  Copy-Item $src (Join-Path $RefDir $dll) -Force
}

# 2) Build our free Photon3Unity3D transport shim, and place it where the mod build
#    references Photon3Unity3D (so the mod compiles against the same free transport
#    the game will load at runtime).
$ShimCsproj = Join-Path $RepoRoot "shims\Photon3Unity3D.Shim\Photon3Unity3D.Shim.csproj"
if (-not $SkipBuild -and (Test-Path $ShimCsproj)) {
  Step "Building free Photon3Unity3D transport shim"
  dotnet build $ShimCsproj -c Release | Out-Null
}
# Prefer the freshly-built shim; fall back to the copy bundled next to this script.
$ShimDll = Pick (Join-Path $RepoRoot "packages\Core\AssemblyReferences\Photon3Unity3D.dll") (Join-Path $PSScriptRoot "Photon3Unity3D.dll")
Need $ShimDll "Photon3Unity3D shim (build shims/Photon3Unity3D.Shim, or use the bundled copy)"
Copy-Item $ShimDll (Join-Path $RefDir "Photon3Unity3D.dll") -Force

# 3) Build the NekoNexus client mod (against your game assemblies + our shim).
if (-not $SkipBuild) {
  Step "Building NekoNexus client mod"
  dotnet build (Join-Path $RepoRoot "packages\Client\Current\NekoNexus.Client.Bootstrap\NekoNexus.Client.Bootstrap.csproj") -c Release | Out-Null
}
# The mod projects emit straight into the client payload folder (custom OutputPath).
$BootstrapBin = Join-Path $RepoRoot ".release\client\_pak\UberStrike_Data\Managed"
if (-not (Test-Path (Join-Path $BootstrapBin "NekoNexus.Client.Bootstrap.dll"))) {
  throw "Built mod DLLs not found in $BootstrapBin. Build may have failed; re-run without -SkipBuild."
}

# 4) Install the NekoNexus runtime + our free transport into the game FIRST. The patch injects a
#    Call into NekoNexus.Client.Bootstrap, so that DLL must already be in Managed for the patcher
#    to resolve it - otherwise the patch fails (exit 1, stops at "patch method Awake").
Step "Installing NekoNexus runtime + free transport into the game"
$installs = @(
  (Join-Path $BootstrapBin "NekoNexus.Client.Bootstrap.dll"),
  (Join-Path $BootstrapBin "NekoNexus.Client.dll"),
  (Join-Path $BootstrapBin "0Harmony.dll"),
  (Join-Path $BootstrapBin "log4net.dll"),
  (Join-Path $BootstrapBin "YamlDotNet.dll")
)
foreach ($f in $installs) { if (Test-Path $f) { Copy-Item $f $Managed -Force; Write-Host "    + $(Split-Path $f -Leaf)" } }

# Our free Photon3Unity3D.dll REPLACES the game's Photon transport (back it up once).
$gamePhoton = Join-Path $Managed "Photon3Unity3D.dll"
if ((Test-Path $gamePhoton) -and -not (Test-Path "$gamePhoton.orig")) { Copy-Item $gamePhoton "$gamePhoton.orig" -Force }
Copy-Item $ShimDll $gamePhoton -Force
Write-Host "    + Photon3Unity3D.dll (free LiteNetLib transport)"

# 5) Inject the bootstrap into Assembly-CSharp.dll (now resolvable). Creates a backup first.
if (-not $NoPatch) {
  Need $PatcherExe "UniversalUnityPatcher.exe (build packages/Patcher first)"
  # If a backup exists, the game was patched before — restore the original first so we always
  # patch a clean Assembly-CSharp.dll (re-patching an already-patched DLL crashes the patcher).
  $backupDll = Join-Path $Managed "backup\Assembly-CSharp.dll"
  if (Test-Path $backupDll) {
    Copy-Item $backupDll (Join-Path $Managed "Assembly-CSharp.dll") -Force
    Write-Host "    restored clean Assembly-CSharp.dll from backup before patching"
  }
  Step "Patching Assembly-CSharp.dll (backup kept in Managed\backup)"
  # UniversalUnityPatcher is a WinForms exe; use Start-Process -Wait so we don't continue
  # before it finishes (a plain call can return immediately and skip the patch).
  # NOTE: -ArgumentList does NOT auto-quote elements with spaces, so quote the paths
  # ourselves (game installs live under "C:\Program Files (x86)\...").
  $pargs = @("--backup", "--no-gui", "--silent", "--ignore-duplicate-patch", "-i", "`"$Managed`"", "-p", "`"$PatchXml`"")
  $p = Start-Process -FilePath $PatcherExe -ArgumentList $pargs -NoNewWindow -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "UniversalUnityPatcher failed (exit $($p.ExitCode)). Is Assembly-CSharp.dll an unmodified, supported build?" }
}

# 6) Write the settings file pointing at your server.
#    -Https: one HTTPS domain (no ports). Otherwise http://host:port.
if ($Https) { $webBase = "https://$ServerHost"; $fileBase = "https://$ServerHost" }
else        { $webBase = "http://${ServerHost}:${WebPort}"; $fileBase = "http://${ServerHost}:${FilePort}" }
Step "Writing NekoNexus.Settings.Client.xml -> $webBase"
$tpl = Get-Content (Join-Path $PSScriptRoot "NekoNexus.Settings.Client.template.xml") -Raw
$tpl = $tpl -replace 'http://SERVER_HOST:8080/2\.0/', "$webBase/2.0/" `
            -replace 'http://SERVER_HOST:8081/images/', "$fileBase/images/" `
            -replace 'http://SERVER_HOST:8081/updates/', "$fileBase/updates/"
Set-Content -Path (Join-Path $DataDir "NekoNexus.Settings.Client.xml") -Value $tpl -Encoding UTF8

Write-Host ""
Write-Host "Done. UberStrike is patched for the free NekoNexus server at $webBase." -ForegroundColor Green
Write-Host "Launch the game; add or switch servers anytime in NekoNexus Settings -> Web Service URLs."
