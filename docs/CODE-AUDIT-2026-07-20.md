# Icebreaker SPT 4.0.13 code audit

**Scope.** Read-only review of the repository as received, with source/API checks
against `D:\SPT400_assembly\Assembly-CSharp` and `D:\server`. No production source,
configuration, or asset was changed. The working tree was already dirty; those changes
were treated as user-owned. This is a static/build audit, not an in-game raid test.

## Executive summary

The project has a strong map-specific recovery strategy: it uses the dormant `Suburbs`
slot, supplies fresh lazy-loaded loose loot per raid, and documents the otherwise
easy-to-miss asset/bundle limitations well. The main release risks are isolation and
reproducibility, rather than ordinary C# correctness.

Several broad Harmony patches are installed for every raid but are intended only for
Icebreaker. Some alter bot setup; others swallow exceptions on core game methods. This
can conceal regressions or change behavior on other maps. The repo also cannot produce
a distributable installation by itself: the scene and four manifest bundles are absent,
and normal builds fail in their deploy phase because they target a personal `D:\SPTDev`
installation.

## Findings (ordered by priority)

| Priority | Finding | Evidence and impact | Recommended direction |
|---|---|---|---|
| P0 | Global bot/culling repairs are not location-gated. | `RaidFixPatches.cs:2370-2389` creates `AIStationaryController` and `ObservedCullingManager`; `:2413-2429` mutates every `BotZone`; `:2605-2625` assigns a nearest core point to any bot missing one. None checks `GameWorld.LocationId == "Suburbs"`. These can modify a normal map or hide its own missing-scene defect. | Put an authoritative Icebreaker guard at every patch entry point, before object discovery/mutation. Test one vanilla map with the plugin enabled after that change. |
| P0 | Exception suppressors are globally active, including a per-frame gameplay method. | `RaidFixPatches.cs:2162-2167`, `:2395-2400`, `:2437-2442`, and `:2634-2637` return `null` for exceptions in core AI/door/motion methods without a map guard. The last one masks every `MotionEffector.FixedTracking` exception, even outside Icebreaker. | Scope finalizers to Icebreaker; when not on Icebreaker, return the original exception. Retain rate-limited diagnostic logging during development rather than silent suppression. |
| P0 | Wave override identifies the map by a non-unique naming convention. | `RaidFixPatches.cs:2543-2564` treats any non-empty wave list whose spawn names start with `BotZone` as Icebreaker. That convention is common to EFT maps, so it may bypass the user's bot amount setting and force Hard difficulty elsewhere. | Gate by the current location id, then retain a map-specific secondary validation only if needed. Add a regression test matrix for another BotZone-based map. |
| P1 | The build/deploy path is machine-specific and makes compilation report failure after successful compilation. | `Directory.Build.props:7`, both project files, and `PerfectCullingRuntime.csproj` reference `D:\SPTDev` or a user desktop SDK. Build source compilation succeeds, but client/server post-build copy fails with access-denied errors to `D:\SPTDev`; PerfectCulling only succeeds because its copy uses `ContinueOnError`. | Separate build from optional deploy/package targets. Parameterize an SPT root with a validated MSBuild property/environment variable; make deploy opt-in and fail clearly only when requested. |
| P1 | The repository does not contain the runtime assets declared by its own data. | `base.json` requires `maps/icebreaker.bundle`; `bundles.json` declares keypad, keypad UI, post-it, and torch bundles. All five are absent from the repository. The client project comment also says large scene assets are staged manually. A fresh clone cannot be installed or verified. | Publish a release manifest/checksum and a repeatable staging/package script, or document an explicit external-artifact source and required destination paths. Verify presence before packaging. |
| P1 | Diagnostics are enabled or installed outside their documented switch. | `Plugin.cs:288-293` handles dump/generate hotkeys regardless of `DiagHotkeys`; `FogTuner.cs:15-23` creates an F9 tuner for every game start; `RaidFixPatches.cs:446-462` creates a persistent camera probe and automatically dumps on every `SetCamera`. F9 conflicts with the default `DumpKey`. This adds cross-map behavior and can create large logs/AI dumps. | Make all diagnostic components map- and config-gated, choose non-conflicting defaults, and disable automatic dumps in release defaults. |
| P1 | The main patch module combines production behavior, compatibility workarounds, probes, and experiments. | `RaidFixPatches.cs` is 2,460 lines and contains camera recovery, audio/lighting/culling, AI synthesis, third-party Waypoints interception, diagnostics, and global exception finalizers. This makes patch ordering and blast radius hard to reason about. | Split by subsystem and lifecycle, with one small guard/helper per map-specific patch. Keep diagnostics and experimental recovery in explicitly opt-in modules. |
| P2 | Server nullability warnings point to real fallback assumptions. | Build reports `IcebreakerMod.cs:134` possible null return from the loose-loot lazy factory and `:191` possible null locale dictionary value. A malformed JSON file can therefore defer an error to raid-generation time; locale changes can fail after some locales have already been transformed. | Validate deserialized data eagerly at load, reject incomplete records with a single actionable message, and skip null locale entries. |
| P2 | SPT 4.1 compatibility is advertised more broadly than the implementation supports. | Metadata accepts `~4.0` (`IcebreakerMod.cs:25`), but the build reports `ConfigServer` and `GetConfig<T>` obsolete and scheduled for removal in 4.1 (`:47`, `:173`). | Pin the released compatibility range to tested versions or migrate to direct configuration injection before declaring 4.1 support. |
| P2 | Dead code and unused state reduce signal during debugging. | Client build warns of unreachable MBOIT code (`IcebreakerWeather.cs:302-304`) and an assigned-but-unused heli flag (`IcebreakerHeliExfil.cs:29`). | Remove/archive dead experiments outside the release assembly and either use or remove stale state when doing the next cleanup pass. |
| P3 | Reflection rehydration is intentionally version-fragile and several required fields are not checked before `SetValue`. | Examples: `IcebreakerAcoustics.cs:440`, `:593-597`, `:635-642`, and `RaidFixPatches.cs:2061`. The supplied 4.0.13 sources support the reviewed APIs today, but an EFT/SPT update can turn a field rename into a late raid-load failure. | Centralize reflected member binding, validate it once at plugin start, report all missing members with the exact game build, and disable only the affected optional subsystem. |
| P3 | A static server RNG is shared by lazy factories. | `IcebreakerMod.cs:203` and `:216+` use one `Random` instance without synchronization. This is low risk for one local raid, but unsafe if factories are reached concurrently. | Use the server's injected random utility or a thread-safe/random-per-request source. |

## Validation performed

- Parsed all five server database JSON files successfully.
- Verified the important client hooks in the supplied 4.0.13 decompiled assembly, including `AICoversData.CachePoints` and the AI controller initialization paths.
- Verified the loose-loot behavior against the supplied SPT server source: `LocationLootGenerator` reads forced spawn points and does not itself implement the project's group-position selection. The fresh-deserialization design is therefore reasonable.
- Built all three projects with `--no-restore`. Source compilation succeeded. Client and server builds ended failed only at their hard-coded deployment copy steps; runtime-culling compiled successfully with a deployment warning. Warnings: client CS0162/CS0414; server CS0618 (twice), CS8603, CS8602.

## Recommended remediation sequence

1. **Containment first:** add a single authoritative `Suburbs` guard to every Icebreaker-specific Harmony prefix/postfix/finalizer and diagnostic component. Do not use scene/object names as the primary discriminator.
2. **Regression test isolation:** launch at least one vanilla map with the plugin present; confirm bot count/difficulty, culling manager, doors, AI caches, motion effectors, and log volume are unchanged. Then raid Icebreaker with bots enabled and disabled.
3. **Build/release pipeline:** make build, deploy, and package independent. Parameterize the SPT and SDK locations, verify artifact existence, and create one distribution layout matching the documented install paths.
4. **Harden optional recovery systems:** introduce a reflected-member capability check and subsystem-level feature flags; replace broad exception swallowing with Icebreaker-only, rate-limited diagnostics.
5. **Clean release defaults:** disable dump/probe/tuner behavior unless explicitly enabled; remove dead MBOIT code and stale heli state after the functional fixes are proven.
6. **Compatibility contract:** either pin the mod to the exact tested SPT version or complete the 4.1 server-DI migration before widening support.

## Open validation needed

The source review cannot establish runtime correctness of the missing scene/preset and custom bundles. Before release, provide the built asset set or a staged SPT installation, then capture one Icebreaker raid and one vanilla raid with BepInEx/server logs. That is required to measure frame time, shader binding, scene-load order, AI activation, loot generation, and third-party-mod interaction.
