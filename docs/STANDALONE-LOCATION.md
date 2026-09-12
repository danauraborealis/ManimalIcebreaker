# Independent Icebreaker location — 1.1.0

Implemented 2026-09-12 against SPT 4.1.5. Icebreaker now registers as `icebreaker`, with permanent location ID `882b2fa04bbd616567022938`. Suburbs keeps its original location object, identity and locale entries.

Install `dist/Manimal-Icebreaker-1.1.0.zip` over the SPT installation root. Fika users also need the matching `Manimal-IcebreakerFika-1.1.0.zip`. Restart both server and client to refresh the cached location list. The matching client, server and database files are required; a client-only DLL update is insufficient. No profile reset or core database edits are needed.

Existing completed quests, crossing counts, quest marks and saved raid deduplication IDs remain valid. New Icebreaker raids continue those counts; new Suburbs raids no longer count as visits to the ship.

## Finding

Icebreaker can be registered independently. The older project comments saying the closed `Locations` record forces reuse of Suburbs do not describe the available registration path in the installed 4.1.5 implementation.

`LocationTable.GetDictionary()` returns its persistent, case-insensitive dictionary, not a copy. `GetLocation(name)` queries that dictionary first. `LocationController.GenerateAll()` enumerates it and returns each base keyed by `IdField` to `/client/locations`.

The registration mechanism is therefore:

```csharp
// During mod startup, after building the complete Location and loading its loot.
locationTable.GetDictionary().Add("icebreaker", icebreakerLocation);
```

Use a new `Location` object. Do not mutate `locationTable.Suburbs`, reuse its `_Id`, or add two aliases for the same location: `GenerateAll()` uses `Dictionary.Add` on `_Id`, so duplicate IDs fail. Detect a preexisting Icebreaker key/ID explicitly instead of overwriting another mod. Adding only `ExtensionData` is insufficient: dictionary hydration enumerates typed location properties, not extension data.

No location-registry reflection patch is needed for this path in 4.1.5. It depends on the inspected implementation and must be rechecked for future SPT versions.

## The map button and preset are separate concerns

The installed client's `MatchMakerSelectionLocationScreen.ShowInternal()` builds `LocationButton` instances from `session.LocationSettings.locations.Values`. Its `DisplayLocation()` accepts enabled locations except the special paired Factory night and Ground Zero high variants. An enabled Icebreaker entry can therefore receive its own button through the normal screen flow.

`LocationButton` uses `<_Id> Name` for the label. Location coordinates derive from `IconX / 1000` and `IconY / 1000`; the current Icebreaker base has `(91, 747)`. Keep those initial coordinates and verify placement in game. Connection paths are separate data; adding a map does not require inventing a transit connection.

The scene preset already exists: `db/base.json` points `Scene.path` at `maps/icebreaker.bundle`. `analysis/patch_icebreaker_preset.py` authors the Icebreaker scene keys, and `IcebreakerBundleHost` serves the preset and scene bundle from the client plugin payload. This loading path is independent of the server's Suburbs identity. A new preset is not the missing registration step here; retain the existing working assets for the first migration.

The optional pocket-map resource lookup is `MapPointConfigs/<location.Id>` and tolerates a missing resource. That resource is distinct from the world-map location button.

## Migration requirements implemented

The implementation follows the requirements below. Mod and location identity are generated from `Directory.Build.props` by `Directory.Build.targets`. The changed menu, fare, gate, keypad and diagnostic hooks use `ModulePatch`; changed server bot/loot entry hooks use `AbstractPatch`. Fika creation, environment setup and extraction bridges also use `ModulePatch`. Existing third-party loot-patch suspension/restoration and unrelated legacy hooks retain their behavior.

1. Define one shared Icebreaker location identity: canonical key/`Id` `icebreaker`, plus a stable unique 24-character hexadecimal `_Id`. Prefer a verified retail Icebreaker ID if compatibility requires it; otherwise allocate a permanent custom ID after checking collisions. Do not ship the probe's fixture ID.
2. In `IcebreakerMod.OnLoadAsync`, populate a new `Location` with the existing base, static ammo, static loot, loose loot, containers and extract data; register it once fully initialized. Preserve the current fresh-per-access loose-loot behavior. Keep Suburbs and its locales unchanged.
3. Give Icebreaker its own locale name and description keys. Change the eight custom quest files whose `location` field currently contains Suburbs' `_Id`. Preserve quest IDs and completed quest state. Inspect any location conditions separately from the quest display location.
4. Move the 40-bot cap and cloned scav raid-time configuration to `icebreaker`. Review other per-map settings. The inspected loot generator falls back to `default` for unlisted static/loose multipliers; choose explicit values if needed to preserve current balance. Verify insurance, raid-time adjustment, bot generation and raid end with the new key.
5. Update `IceGate`, direct location comparisons in `RaidFixPatches`, `IcebreakerMapUnlock`, `IcebreakerMapFare`, `IcebreakerLootDiag`, and `Keypad/IcebreakerPasscodes`. Keep the Boreas unlock, full/discounted fare, scav exclusion and all map-scoped fixes. Client changes must use loops rather than LINQ.
6. Update the server loot isolation check and knight-spawn restoration lookup, PBS compatibility mapping and visit-ledger raid-end recognition. Change the profile-specific location-lock router to the new `_Id`. Preserve saved visit counts, quest marks and old raid deduplication IDs; new Suburbs raids must not count as Icebreaker visits.
7. Verify Fika host, joining client and headless paths using the shared identity and fare logic. Preserve existing SAIN/PBS/other-mod compatibility handling: custom map names can still fail in third-party hardcoded map tables even though core SPT accepts them.
8. Update the base-generation script and verification fixtures so future regeneration cannot restore the Suburbs identity. Any runtime patch changes must use the SPT patch wrapper and retain existing patch coverage.

## Evidence and verification

The Release client, server and Fika addon build successfully. The full offline verification suite passed 195 checks: real mod startup and map-list generation; Suburbs preservation; collision rejection; compiled client/server/Fika metadata and identity; authored loot and fresh per-raid loot objects; bot cap, native PMC wave handling and seasonal-event hostility protection; profile-specific unlocks; saved crossing history and new raid-end identity; loot isolation; 89 remaining attribute-based patch targets; ten location-related SPT wrapper targets/argument bindings; and Fika packet round trips.

The client reflection checks use a .NET-compatible Harmony assembly inside the test runner. They do not launch Unity or install hooks into a running client. Run the suite after building with:

```powershell
dotnet build icebreaker-fika/icebreaker-fika.csproj -c Release -p:DeployToGame=false
dotnet build verification/verification.csproj -c Release
dotnet verification/bin/Release/net10.0/verification.dll D:/SPT41Dev icebreaker-client/bin/Release/netstandard2.1/ManimalIcebreakerClient.dll D:/SPT41DevFika icebreaker-fika/bin/Release/netstandard2.1/ManimalIcebreakerFika.dll
```

An in-game solo/Fika raid is still needed to validate placement, entry/fare, scene loading, late waves, keypads and extraction under the new identity. These changes have not been published or deployed to the development game installations.

`analysis/location-probe` is a package-free console probe using the installed SPT runtime assemblies. It creates synthetic in-memory locations and exercises the actual `LocationTable` and `LocationController`; it does not start SPT or touch player profiles/core database files.

```powershell
dotnet restore analysis/location-probe/LocationProbe.csproj --configfile analysis/location-probe/NuGet.Config
dotnet run --project analysis/location-probe/LocationProbe.csproj -c Release --no-restore
```

Result: build succeeded with zero warnings/errors and all seven checks passed. Checks cover persistent dictionary identity, lowercase and mixed-case lookup, independent Suburbs preservation, exactly one additional map in `GenerateAll`, and both unique map IDs in the response. These establish the server registration path, not end-to-end raid compatibility.

Before releasing a migration, test locked/unlocked profiles, the menu after completing Boreas without relogging, fare and discount, solo and Fika loads, loot and late bot waves, keypads, extraction/death/MIA and visit progression, then a vanilla map. Confirm Suburbs remains its original disabled stub. Validate compiled metadata and runtime-only archive contents; keep this probe and document out of install packages.

Primary implementation evidence: installed `D:/SPT41Dev/SPT_Runtime/SPTarkov.Server.Core.dll` (`LocationTable`, `LocationController`) and `D:/SPT41Dev/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll` (`MatchMakerSelectionLocationScreen`). The official [SPT 4.0-to-4.1 migration guide](https://github.com/sp-tarkov/wiki/blob/main/SPT_41/Server_40_to_41.md) documents the injectable `LocationTable` and moved `GetLocation` API; the mutable dictionary behavior was verified locally, not inferred from that guide.
