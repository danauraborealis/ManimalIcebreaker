# Port Icebreaker to SPT 4.1.3

The 4.0.13 client/server APIs prevent this version from loading on 4.1.3. This contribution updates the applied port and includes the selected gameplay/runtime changes used by the tester.

## Implementation notes

- Server: .NET 10, IModMetadata, CancellationToken-aware OnLoadAsync, current namespaces and direct injection of tables/services. The nonvirtual loot and bot generators require Harmony hooks instead of the old subclass overrides. Loot isolation retains its foreign-patch restoration and re-entry guard; this is sensitive global patching and deserves particular maintainer review.
- Client: map renamed game classes and members, retarget Harmony hooks, update TODSkyProvider, inventory interactions, blowtorch and environment/AI calls to the installed 4.1.3 API.
- Preserve the selected non-revert changes from danyhappy564-cmyk/ManimalIcebreaker: per-world caches/cleanup, chunked LOD and distance-culling work, Wedge restart/cover handling, duplicate SnowGust protection and reduced scene scans. No measured FPS improvement is claimed.
- Remove seven exact duplicate waves: 33 unique waves remain. Restore b9e951b's helipad group: stern0 / BotZoneStern has one leader plus five escorts.
- Set EscapeTimeLimit, EscapeTimeLimitCoop and EscapeTimeLimitPVE to 600 minutes. Event-wave fallback timers become 39600 seconds, beyond the ten-hour raid, so untriggered encounters do not automatically spawn at the old 9999-second deadline. This is an intentional gameplay default, not required by the API migration.
- Keep original map-lock quest conditions in source. The test-only unlock, profiles and currency edits are not part of this contribution.

## Provenance

Port base: b5799b192d18ea2f80ef2bccdc1e46fad08dce94. Selected fork history: https://github.com/danyhappy564-cmyk/ManimalIcebreaker/tree/c7e4d655f8bdd146635deca2319230f7584687e1 . Explicit revert commits were not replayed. Detailed selection is in docs/FORK-SELECTION.md.

Map Unity bundles were used unchanged from the tester's original 0.3.1 archive; the eight installed bundles matched that archive by SHA256. This PR does not redistribute the game assemblies, that archive or private test data.

## Validation specific to this mod

Prior client/server builds passed; static inspection checked Harmony/reflection targets. The running server response after SVM loaded showed all three limits at 600, 33 waves and the restored 1+5 helipad wave. Bot generation endpoints returned populated custom roles. New contribution builds are recorded in the PR validation section.

Required companion ports: MoreBotsAPI, BlackDiv and ManimalCSGas. Tested with BigBrain 1.5.0, WTT CommonLib 3.0.6, SAIN 4.5.0, Ladders 1.0.4 and Backport 2.0.1 from 4.1.x-dev. Fika code contains API adjustments but multiplayer was not validated.

## Compatibility and evidence

Target: **SPT 4.1.3**, EFT **0.16.9.5.40743**. This is a source contribution for that environment, not a claim of compatibility with future SPT releases or Fika.

The tester successfully loaded Icebreaker, entered a raid and extracted. In subsequent feedback they confirmed the blowtorch, extraction and doors work, and Black Division bots appeared to behave normally after the SAIN fix. These are user-reported functional observations, not automated coverage of every encounter or performance benchmarks. Ten continuous hours, all quests and multiplayer have not been tested.

The migration used installed SPT 4.1.3 assemblies and these guides:
- https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/Server_413_Changes
- https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/client/Class_Name_Mappings
- https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/server/Mod_Web_Pages

## Build and packaging

Use the .NET 10 SDK and an installed SPT 4.1.3 dependency set. Pass `-p:SPTPath=<installation-root>` to dotnet build; the fallback expects this repository under a development tree. DeployToGame defaults to disabled. Build the client and server projects in Release, and the companion dependency ports first. Proprietary game DLLs are local references and must not be committed.


## Contribution build checks

The publication working copies were built in Release against the installed SPT 4.1.3 assemblies with deployment disabled. Client/server builds passed for Icebreaker, MoreBotsAPI, BlackDiv and ManimalCSGas; the Backport prepatcher and DynamicMaps client also passed. Existing compiler warnings remain in several ports. Fika was not built as part of this contribution gate. These checks validate compilation, not untested gameplay.
