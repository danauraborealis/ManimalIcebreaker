# SPT 4.1 migration — Icebreaker 1.0.0

The main client and server target SPT 4.1.5. The optional Fika addon builds against Fika 2.4.2 in `D:\SPT41DevFika`; release packaging builds its separate ZIP by default. Use `-IncludeFika:$false` to skip the addon. Host/client raid testing remains outstanding.

## Dependencies

| Dependency | Required release |
| --- | --- |
| WTT CommonLib | 3.0.6 |
| WTT Content Backport | 2.0.1 |
| Black Division | 1.3.1 |
| MoreBotsAPI | 2.1.1 |
| BigBrain | 1.5.0 |
| SAIN | 4.5.1 |
| Manimal CS Gas | 2.0.0 |

The existing ladders requirement remains; install its SPT 4.1 release too. CS Gas 2.0.0 is required on both client and server. Client requirements enforce minimum versions; server ranges stay within the supported minor versions.

Sources: [server migration](https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/Server_40_to_41), [client mappings](https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/client/Class_Name_Mappings), [CommonLib](https://github.com/WelcomeToThursday/WTT-CommonLib/releases/tag/v3.0.6), [Content Backport](https://sp-mod.com/mod/2512/wtt-content-backport), [Black Division 1.3.1](https://github.com/TacticalToaster/BlackDiv/releases/tag/1.3.1), [MoreBotsAPI 2.1.1](https://github.com/TacticalToaster/MoreBotsAPI/releases/tag/2.1.1). Installed 4.1 plugin metadata also confirms these versions and Black Division's SAIN requirement.

## Building

Requires .NET 10 SDK and SPT 4.1.5 assemblies. `Directory.Build.props` defaults to `D:\SPT41Dev` and its `SPT_Runtime` directory. Override `SPTPath`, `SPTServerPath`, `BigBrainPath`, or `FikaPath` with MSBuild properties when needed. Builds stay in the workspace unless `-p:DeployToGame=true` is explicitly supplied.

```powershell
dotnet build icebreaker-client/icebreaker-client.csproj -c Release
dotnet build icebreaker-server/icebreaker-server.csproj -c Release
dotnet run --project verification/verification.csproj -c Release -- D:\SPT41Dev icebreaker-client/bin/Release/netstandard2.1/ManimalIcebreakerClient.dll
dotnet build icebreaker-fika/icebreaker-fika.csproj -c Release
dotnet run --project verification/verification.csproj -c Release -- D:\SPT41Dev icebreaker-client/bin/Release/netstandard2.1/ManimalIcebreakerClient.dll D:\SPT41DevFika icebreaker-fika/bin/Release/netstandard2.1/ManimalIcebreakerFika.dll
```

BigBrain 1.5.0 is now installed in the local 4.1 install. The initial migration used the official DLL downloaded into `.tmp/bigbrain/BepInEx/plugins` as a temporary build reference.

Full release packaging can reuse the authored bundle payload from the old dev install while compiling code against 4.1:

```powershell
.\package-release.ps1 -ValidateOnly
.\package-release.ps1
.\package-release.ps1 -OutputDirectory .\dist\raid-fixes
.\package-release.ps1 -IncludeFika -OutputDirectory .\dist\fika-4.1
```

By default, the script looks for a complete authored payload in the SPT 4.1 install and then in `D:\SPTDev`. It prefers BigBrain from the 4.1 install and falls back to the downloaded `.tmp/bigbrain` reference. `-AssetSourcePath` and `-BigBrainPath` remain available for other layouts. The script refreshes the repository's plugin-data backup from the asset source, as the previous release workflow did. Fika packaging is enabled by default, requires a compatible Fika DLL, and produces the separate addon archive; `-IncludeFika:$false` disables it. Main archives contain `BepInEx` and `SPT_Runtime` trees; installations with a different runtime directory must relocate the server mod folder.

## Changes and verification

- Replaced obfuscated client types, renamed members and reflection lookups, and selected explicit interaction-method overloads.
- Migrated metadata to `IModMetadata`, lifecycle methods to `OnLoadAsync`, routers to cancellation-aware actions with explicit priorities, and database/config access to injected tables and configs.
- Registered items after Content Backport's preload, and retained fresh loose-loot deserialization with `cacheValue: false`.
- Replaced removed virtual generator overrides with Harmony hooks. Loot isolation wraps the call in `LocationLifecycleService`, so foreign generator patches are suspended before the generator is entered and restored afterward.
- Client, server and optional Fika Release builds pass. The verification runner checks dependency compatibility, installs the actual server loot transpiler, verifies its failure behavior, and resolves 90 main-client Harmony targets (94 including the addon) and their named parameter bindings against the installed assemblies. It also confirms native tripwire setup calls the factory overload repaired by the tripwire follow-up.
- Startup splash selection includes `Texture2D/splash_17.png` and `splash_18.png` alongside the original candidates. Both PNGs are embedded in the client DLL and must remain tracked source assets. The existing startup canvas and fade timing are preserved, including when the panel initialized before the plugin. Each image is an additional random candidate, so an Icebreaker splash is not guaranteed on every launch. Embedded image bytes were checked against the source PNGs; visual startup testing remains required.
- The Fika door diagnostic now targets `FikaPlayer.ExecuteInteraction`; the old `vmethod_1` name no longer exists. The addon requires Fika 2.4.2 or newer and Icebreaker 1.0.0 or newer. Weather guards are restricted to Icebreaker.
- Optional Fika verification checks the host/raid-seed/human-player reflection interfaces and packet cleanup fields. It also round-trips the built `IceWorldPacket` through the installed Fika serializer, covering null/Unicode strings, booleans, integer limits and float values without starting a raid or opening a network connection.

The first user raid completed, revealing scope weather, Wedge AI and T4 spawning issues. See [raid follow-up](SPT-4.1-RAID-FIXES.md) for the fixes and remaining gameplay checks. The updated client DLL has been deployed to `D:\SPT41Dev` with the preceding DLL backed up in `.tmp/ManimalIcebreakerClient.pre-raid-fixes.dll`. Successful compilation, metadata and packet checks do not establish raid or co-op compatibility. The user is copying mods into the new Fika installation; no mods were deployed there by this migration step. Test host/client loading, synchronized doors/keypads/hatches/torch/extraction, matching tripwires, and host-only bot spawning next.
