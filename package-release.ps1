# builds both halves in Release and produces THE release zip in dist\ — single archive,
# same format as the other manimal mods: BepInEx\ + SPT\ trees at the root (plus
# EscapeFromTarkov_Data\ — the 1.8GB scene bundle's only legal home), extracted over
# the SPT install root.
# (the per-build zip next to the client csproj is DLL-only — an update patch, not a release)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# version + spt path from the single source of truth
[xml]$props = Get-Content "$root\Directory.Build.props"
$ver = $props.Project.PropertyGroup.ModVersion
$spt = $props.Project.PropertyGroup.SPTPath

Write-Host "=== building client + fika addon + server (Release) ===" -ForegroundColor Cyan
# the fika addon build also builds the client (project reference); its PostBuild
# deploys the addon dll into the live plugin dir, where the staging below harvests it
dotnet build "$root\icebreaker-fika\icebreaker-fika.csproj" -c Release -v m
if ($LASTEXITCODE -ne 0) { throw "client/fika build failed" }
dotnet build "$root\icebreaker-server\icebreaker-server.csproj" -c Release -v m
if ($LASTEXITCODE -ne 0) { throw "server build failed" }

Write-Host "=== staging ===" -ForegroundColor Cyan
$stage = "$root\obj-release-stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

# BepInEx\plugins payload: the LIVE dev deploy is the canonical copy of the authored
# data (acoustics/aibake/aiplaces/culling/cutscene/flares/weather/jsons/volumetricfog
# + PerfectCullingRuntime) — those files exist nowhere in the repo. dev debris stays out.
$pluginDst = "$stage\BepInEx\plugins\ManimalIcebreaker"
New-Item -ItemType Directory -Force $pluginDst | Out-Null
Copy-Item "$spt\BepInEx\plugins\ManimalIcebreaker\*" $pluginDst -Recurse -Force
Remove-Item "$pluginDst\dumps" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $pluginDst -Recurse -File -Filter '*.bak*' | Remove-Item -Force
# fresh DLL from this build, not whatever the deploy dir held
Copy-Item "$root\icebreaker-client\bin\Release\netstandard2.1\ManimalIcebreakerClient.dll" $pluginDst -Force

# SPT\user\mods payload: db + bundles.json from the repo (their source of truth), item
# bundles from the live server mod dir (staged there by hand from the SDK)
$serverDst = "$stage\SPT\user\mods\ManimalIcebreaker"
New-Item -ItemType Directory -Force $serverDst | Out-Null
Copy-Item "$root\icebreaker-server\db" "$serverDst\db" -Recurse -Force
Copy-Item "$root\icebreaker-server\bundles.json" $serverDst -Force
Copy-Item "$spt\SPT\user\mods\ManimalIcebreaker\bundles" "$serverDst\bundles" -Recurse -Force
Copy-Item "$root\icebreaker-server\bin\Release\icebreaker-server.dll" $serverDst -Force

# EscapeFromTarkov_Data: scene bundle + manifest, map preset, audio bake
$sa = "$stage\EscapeFromTarkov_Data\StreamingAssets"
New-Item -ItemType Directory -Force "$sa\Windows\assets\content\locations\icebreaker", "$sa\Windows\maps", "$sa\AudioBakeData" | Out-Null
Copy-Item "$spt\EscapeFromTarkov_Data\StreamingAssets\Windows\assets\content\locations\icebreaker\icebreaker_scenes.bundle" "$sa\Windows\assets\content\locations\icebreaker\" -Force
Copy-Item "$spt\EscapeFromTarkov_Data\StreamingAssets\Windows\assets\content\locations\icebreaker\icebreaker_scenes.bundle.manifest" "$sa\Windows\assets\content\locations\icebreaker\" -Force
Copy-Item "$spt\EscapeFromTarkov_Data\StreamingAssets\Windows\maps\icebreaker.bundle" "$sa\Windows\maps\" -Force
Copy-Item "$spt\EscapeFromTarkov_Data\StreamingAssets\AudioBakeData\icebreaker_sound.audiobakedata" "$sa\AudioBakeData\" -Force

Copy-Item "$root\docs\RELEASE-README.txt" "$stage\README.txt" -Force

Write-Host "=== zipping (the ~2GB scene bundle makes this take a few minutes) ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$zip = "$root\dist\Manimal-Icebreaker-$ver.zip"
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

Write-Host "=== dist ===" -ForegroundColor Green
Get-ChildItem "$root\dist" | Format-Table Name, @{n='Size';e={'{0:N1} MB' -f ($_.Length/1MB)}}
