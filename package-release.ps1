# builds both halves in Release and produces THE release zip in dist\ -- single archive,
# SPT 4.1 layout: BepInEx\ + SPT_Runtime\ trees at the root, extracted
# over the SPT install root. NOTHING under EscapeFromTarkov_Data (forge rule, 07-30) --
# the scene bundle rides the plugin payload and is loaded directly from there.
# (the per-build zip next to the client csproj is DLL-only -- an update patch, not a release)
# Also builds the separate Fika addon by default; use -IncludeFika:$false to skip it.
param(
    [string]$AssetSourcePath,
    [string]$BigBrainPath,
    [string]$OutputDirectory,
    [switch]$IncludeFika = $true,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function Assert-StagePath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    $allowed = @('obj-release-stage', 'obj-fika-stage', 'obj-vt-stage') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $root $_)) }
    if ($resolved -notin $allowed) { throw "Unexpected staging directory: $resolved" }
}

# version + spt path from the single source of truth
[xml]$props = Get-Content "$root\Directory.Build.props"
$ver = $props.Project.PropertyGroup.ModVersion
$spt = $props.Project.PropertyGroup.SPTPath.InnerText
$serverPath = ([string]$props.Project.PropertyGroup.SPTServerPath.InnerText).Replace('$(SPTPath)', $spt)
$fikaPath = ([string]$props.Project.PropertyGroup.FikaPath.InnerText).Replace('$(SPTPath)', $spt)

# The SPT 4.1 install supplies compile-time assemblies, while the authored map payload
# may still live in the 4.0 authoring install. Prefer a complete 4.1 payload when one is
# present, then fall back to the established authoring install.
if (-not $AssetSourcePath) {
    $assetCandidates = @($spt, 'D:\SPTDev') | Select-Object -Unique
    foreach ($candidate in $assetCandidates) {
        $candidateServerPath = Join-Path $candidate 'SPT_Runtime'
        if (-not (Test-Path -LiteralPath $candidateServerPath)) { $candidateServerPath = Join-Path $candidate 'SPT' }
        if (
            (Test-Path -LiteralPath (Join-Path $candidate 'BepInEx\plugins\ManimalIcebreaker\streamingassets')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'BepInEx\plugins\ManimalIcebreaker\PerfectCullingRuntime.dll')) -and
            (Test-Path -LiteralPath (Join-Path $candidateServerPath 'user\mods\ManimalIcebreaker\bundles'))
        ) {
            $AssetSourcePath = $candidate
            break
        }
    }
}
if (-not $AssetSourcePath) {
    throw 'Could not find the authored Icebreaker payload. Pass -AssetSourcePath with the SPT install that contains it.'
}
$AssetSourcePath = (Resolve-Path -LiteralPath $AssetSourcePath).Path
$assetServerPath = Join-Path $AssetSourcePath 'SPT_Runtime'
if (-not (Test-Path -LiteralPath $assetServerPath)) { $assetServerPath = Join-Path $AssetSourcePath 'SPT' }

# Prefer the installed SPT 4.1 dependency. The cache fallback keeps a clean SPT 4.1
# authoring install usable before BigBrain has been copied into it.
if (-not $BigBrainPath) {
    $bigBrainCandidates = @(
        "$spt\BepInEx\plugins\DrakiaXYZ-BigBrain.dll",
        "$root\.tmp\bigbrain\BepInEx\plugins\DrakiaXYZ-BigBrain.dll"
    ) | Select-Object -Unique
    $BigBrainPath = $bigBrainCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $BigBrainPath) {
    throw 'Could not find BigBrain 1.5.0+. Install it in the SPT 4.1 dev instance or pass -BigBrainPath.'
}
$BigBrainPath = (Resolve-Path -LiteralPath $BigBrainPath).Path
$required = @(
    "$spt\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll",
    $BigBrainPath,
    "$serverPath\SPTarkov.Server.Core.dll",
    "$AssetSourcePath\BepInEx\plugins\ManimalIcebreaker\streamingassets",
    "$AssetSourcePath\BepInEx\plugins\ManimalIcebreaker\PerfectCullingRuntime.dll",
    "$assetServerPath\user\mods\ManimalIcebreaker\bundles"
)
if ($IncludeFika) { $required += "$fikaPath\BepInEx\plugins\Fika\Fika.Core.dll" }
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing release input: $path. Use -AssetSourcePath for authored bundles; -IncludeFika requires an SPT 4.1 Fika install." }
}
$coreVersion = [Reflection.AssemblyName]::GetAssemblyName("$serverPath\SPTarkov.Server.Core.dll").Version
if ($coreVersion.Major -ne 4 -or $coreVersion.Minor -ne 1 -or $coreVersion.Build -lt 5) { throw "SPT 4.1.5+ required; found $coreVersion" }
$bigBrainVersion = [Reflection.AssemblyName]::GetAssemblyName($BigBrainPath).Version
if ($bigBrainVersion -lt [Version]'1.5.0.0') { throw "BigBrain 1.5.0+ required; found $bigBrainVersion at $BigBrainPath" }
Write-Host "Release assets: $AssetSourcePath" -ForegroundColor DarkGray
Write-Host "BigBrain reference: $BigBrainPath ($bigBrainVersion)" -ForegroundColor DarkGray
if ($ValidateOnly) { Write-Host "Release inputs verified for SPT $coreVersion (Fika: $IncludeFika)"; return }

Write-Host "=== building SPT 4.1 client + server (Release) ===" -ForegroundColor Cyan
# The optional Fika addon builds the client through its project reference.
# Packaging takes fresh DLLs from build output; it does not deploy into the game.
$clientProject = if ($IncludeFika) { 'icebreaker-fika\icebreaker-fika.csproj' } else { 'icebreaker-client\icebreaker-client.csproj' }
dotnet build "$root\$clientProject" -c Release -v m -p:DeployToGame=false "-p:BigBrainPath=$BigBrainPath"
if ($LASTEXITCODE -ne 0) { throw "client/fika build failed" }
dotnet build "$root\icebreaker-server\icebreaker-server.csproj" -c Release -v m -p:DeployToGame=false
if ($LASTEXITCODE -ne 0) { throw "server build failed" }

# REPO BACKUP REFRESH: mirror the live deploy's data sidecars into
# icebreaker-client\plugin-data so the repo copy can never be stale at release time.
# same exclusions as the backup convention: no dlls, no bundles, no streamingassets.
Write-Host "=== refreshing repo plugin-data backup ===" -ForegroundColor Cyan
$pdSrc = "$AssetSourcePath\BepInEx\plugins\ManimalIcebreaker"
$pdDst = "$root\icebreaker-client\plugin-data"
robocopy $pdSrc $pdDst /MIR /XD streamingassets dumps /XF *.dll *.bundle *.manifest *.bak* README.md /NJH /NJS /NDL /NFL | Out-Null
# robocopy /MIR would delete README.md from the destination since the source lacks it;
# the /XF above shields it from the mirror. exit codes 0-7 are all success flavors.
if ($LASTEXITCODE -gt 7) { throw "plugin-data backup refresh failed (robocopy exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

Write-Host "=== staging ===" -ForegroundColor Cyan
$stage = "$root\obj-release-stage"
Assert-StagePath $stage
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

# BepInEx\plugins payload: the LIVE dev deploy is the canonical copy of the authored
# data (acoustics/aibake/aiplaces/culling/cutscene/flares/weather/jsons/volumetricfog
# + PerfectCullingRuntime) -- the repo's icebreaker-client\plugin-data is a MIRROR of it,
# refreshed above, not the source. dev debris stays out.
$pluginDst = "$stage\BepInEx\plugins\ManimalIcebreaker"
New-Item -ItemType Directory -Force $pluginDst | Out-Null
Copy-Item "$AssetSourcePath\BepInEx\plugins\ManimalIcebreaker\*" $pluginDst -Recurse -Force
Remove-Item "$pluginDst\dumps" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $pluginDst -Recurse -File -Filter '*.bak*' | Remove-Item -Force
# the fika addon ships as its OWN zip (built below) -- never in the main package
Remove-Item "$pluginDst\ManimalIcebreakerFika.dll" -Force -ErrorAction SilentlyContinue
# fresh DLL from this build, not whatever the deploy dir held
Copy-Item "$root\icebreaker-client\bin\Release\netstandard2.1\ManimalIcebreakerClient.dll" $pluginDst -Force

# SPT_Runtime\user\mods payload: db + bundles.json from the repo, item
# bundles from the live server mod dir (staged there by hand from the SDK)
$serverDst = "$stage\SPT_Runtime\user\mods\ManimalIcebreaker"
New-Item -ItemType Directory -Force $serverDst | Out-Null
Copy-Item "$root\icebreaker-server\db" "$serverDst\db" -Recurse -Force
Get-ChildItem "$serverDst\db" -Recurse -File -Filter '*.bak*' | Remove-Item -Force
# Per-profile progression belongs to the installed server, never an update archive.
Get-ChildItem "$serverDst\db" -File -Filter 'icebreaker_visits.json*' | Remove-Item -Force
Copy-Item "$root\icebreaker-server\bundles.json" $serverDst -Force
Copy-Item "$assetServerPath\user\mods\ManimalIcebreaker\bundles" "$serverDst\bundles" -Recurse -Force
Copy-Item "$root\icebreaker-server\bin\Release\icebreaker-server.dll" $serverDst -Force

# FORGE COMPLIANCE: nothing ships under EscapeFromTarkov_Data. the map bundles ride
# the plugin folder's streamingassets/ payload (harvested above with the rest of the
# plugin dir) and the plugin loads them directly from that folder.

# Install archives contain runtime files only; development/release documents
# remain in the repository. Also filter extras inherited from a live asset source.
Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.Extension -in '.md', '.xml', '.cs', '.csproj', '.pdb' -or
    $_.Name -match '^(README|LICENSE|CHANGELOG|RELEASE[-_ ]?INFO|VERSIONING)(\.|$|[-_ ])'
} | Remove-Item -Force

# Record the binaries in the package itself before the staging tree is removed. This
# makes the VirusTotal backstop compare against the actual release rather than a live
# source directory that may contain unrelated or stale files.
$shippedBinaryNames = Get-ChildItem $stage -Recurse -Include *.dll, *.exe -File |
    Where-Object { $_.Name -ne 'ManimalIcebreakerFika.dll' } |
    Select-Object -ExpandProperty Name -Unique

Write-Host "=== zipping (the ~2GB scene bundle makes this take a few minutes) ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$zip = "$OutputDirectory\Manimal-Icebreaker-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# entry-by-entry, NOT CreateFromDirectory: powershell 5.1's framework build writes
# backslash separators into entry names, which is off-spec and trips some extractors
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1) -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Remove-Item -Recurse -Force $stage

# the FIKA SYNC ADDON: its own installable zip, uploaded separately as an addon.
# hard bepinex deps on fika + the main mod mean it's inert anywhere it doesn't belong.
if ($IncludeFika) {
Write-Host "=== packaging fika addon ===" -ForegroundColor Cyan
$fikaStage = "$root\obj-fika-stage"
Assert-StagePath $fikaStage
if (Test-Path $fikaStage) { Remove-Item -Recurse -Force $fikaStage }
$fikaDst = "$fikaStage\BepInEx\plugins\ManimalIcebreaker"
New-Item -ItemType Directory -Force $fikaDst | Out-Null
Copy-Item "$root\icebreaker-fika\bin\Release\netstandard2.1\ManimalIcebreakerFika.dll" $fikaDst -Force
$fikaZip = "$OutputDirectory\Manimal-IcebreakerFika-$ver.zip"
if (Test-Path $fikaZip) { Remove-Item $fikaZip -Force }
$fa = [System.IO.Compression.ZipFile]::Open($fikaZip, 'Create')
try {
    Get-ChildItem $fikaStage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($fikaStage.Length + 1) -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($fa, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $fa.Dispose() }
Remove-Item -Recurse -Force $fikaStage

}

# THE VIRUSTOTAL ARCHIVE. forge wants a scan link per version, and the release zip is
# ~1.6GB of unity asset bundles -- far past what virustotal accepts, and pointless to scan
# anyway since none of it executes. the compiled binaries (including the client's
# embedded splash images) are much smaller, so scan THOSE and link the results.
#
# PerfectCullingRuntime.dll is in here because we REDISTRIBUTE it -- stock, unmodified,
# from the asset store purchase (the multi-scene bake work is a separate editor script in
# the SDK and never touched this assembly). the rule is about what ships, not about what
# we wrote, so a third-party binary in the zip is exactly what a scan link is for.
# volumetricfog.bundle is deliberately NOT here: unity asset bundle, assets only, no
# managed code to analyse.
Write-Host "=== packaging binaries for virustotal ===" -ForegroundColor Cyan
$vtStage = "$root\obj-vt-stage"
Assert-StagePath $vtStage
if (Test-Path $vtStage) { Remove-Item -Recurse -Force $vtStage }
New-Item -ItemType Directory -Force $vtStage | Out-Null
@(
    "$root\icebreaker-client\bin\Release\netstandard2.1\ManimalIcebreakerClient.dll",
    "$root\icebreaker-server\bin\Release\icebreaker-server.dll",
    "$AssetSourcePath\BepInEx\plugins\ManimalIcebreaker\PerfectCullingRuntime.dll"
) | ForEach-Object {
    if (-not (Test-Path $_)) { throw "virustotal archive: missing $_ -- build all three projects first" }
    Copy-Item $_ $vtStage -Force
}

if ($IncludeFika) { Copy-Item "$root\icebreaker-fika\bin\Release\netstandard2.1\ManimalIcebreakerFika.dll" $vtStage -Force }

# BACKSTOP: if a new binary ever lands in the shipped archive, this catches it
# rather than letting it go out unscanned. the scan archive silently missing a dll is the
# failure mode worth guarding, since nothing else in the pipeline would notice.
$staged = Get-ChildItem $vtStage -File | Select-Object -ExpandProperty Name
$unscanned = $shippedBinaryNames | Where-Object { $_ -notin $staged }
if ($unscanned) { throw "binaries shipped but NOT in the virustotal archive: $($unscanned -join ', ')" }
$vtZip = "$OutputDirectory\Manimal-Icebreaker-binaries-$ver.zip"
if (Test-Path $vtZip) { Remove-Item $vtZip -Force }
$va = [System.IO.Compression.ZipFile]::Open($vtZip, 'Create')
try {
    Get-ChildItem $vtStage -File | ForEach-Object {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($va, $_.FullName, $_.Name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $va.Dispose() }
Remove-Item -Recurse -Force $vtStage

# sha256 of everything shipped: paste alongside the scan link so anyone can confirm the
# dll they downloaded is the dll that was scanned
Write-Host "=== sha256 (for the release notes) ===" -ForegroundColor Cyan
$releaseArtifacts = @($zip, $vtZip)
if ($IncludeFika) { $releaseArtifacts += $fikaZip }
$releaseArtifacts | ForEach-Object { Get-Item -LiteralPath $_ } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Tee-Object -FilePath "$OutputDirectory\SHA256SUMS.txt"

Write-Host "=== dist ===" -ForegroundColor Green
@($releaseArtifacts) + "$OutputDirectory\SHA256SUMS.txt" |
    ForEach-Object { Get-Item -LiteralPath $_ } |
    Format-Table Name, @{n='Size';e={'{0:N1} MB' -f ($_.Length/1MB)}}
