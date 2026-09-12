# SPT 4.1 raid follow-up

The September 6 raid log shows the T4 event followed by one Black Division brain
activation, then four more later. The 4.1 `BotBossSpawn` implementation explains
the split: non-start waves wait for the leader before creating escorts, pass
`forceSpawn=false` to their spawn queue, and duplicate the leader's spawn point.

## Changes

- Magnified optics now have their own Volumetric Fog & Mist renderer and GlobalFog
  effect, synchronized after the native optic updater. The optic uses its own
  projection and depth, shares fog areas and exclusion volumes, and follows the
  main camera's indoor fade and live configuration. Native TOD/MBOIT scattering
  stays disabled because it produced blue/black scope views on this map.
- The optic draws enabled native snow meshes in a dedicated transparent pass,
  retaining their materials, weather mask and storm settings. Their automatic
  draw is suppressed only during that camera's render to prevent double density.
  Renderer state and command buffers are restored/released on render completion
  and camera disable.
- Removed the unfinished Wedge rooms, blood-ambush and escort-hold layers. They
  could take priority over normal combat/search with no viable cover fallback.
  Also removed their hit and squad-death perception hooks. Wedge retains close
  taunts; Black Division's underlying brain controls combat and movement.
  The recovered retail 1.0 sources remain in `docs/wedge-ai-src` as reference;
  their incomplete method bodies do not establish a complete retail AI port.
- T4 prepares and warms five individual profiles before submitting any activation.
  It uses the normal low-level spawner and shared group parameters, bypassing the
  delayed escort path for this wave only. The zone has only two authored markers,
  so additional positions are sampled on reachable NavMesh nearby on the same
  deck, at least 1.5 m apart. Occupied, directly exposed and very close positions
  are rejected. If five safe positions are unavailable, the whole squad retries;
  it never intentionally sends one member ahead. Unity's activation work remains
  asynchronous, so this does not promise all five finish on the same rendered frame.

## Raid checks still required

### September 7: headless loading and duplicate loot candidates

Visual setup now checks the local process's `FikaBackendUtils.IsHeadless` flag
and Unity's null graphics device. It does not use `IsHeadlessGame` or
`IsHeadlessRequester`, which would also disable graphics for ordinary players.
Weather reconstruction, camera grafting/repair, the render probe, fog, flares,
volumetric lights, snow gusts, splash images and cutscene playback are skipped
on a dedicated host. A GameWorld-owned startup path still initializes crew,
helicopter exfiltration, the door registry and removal of fog-marker colliders.
The existing Fika weather-request fallback supplies weather nodes without
requiring a graphics WeatherController on the host.

Review of the community `Icebreaker headless crash fix.zip` identified an
additional visual entry point: helicopter material repairs run from the exfil
service even without RenderEnvProbe. That routine now also checks CanRender.
The community DLL was inspected as metadata/decompiled code, not installed.

The exact key reported by Lots of Loot, `504411994`, was duplicated in our
loose-loot data. The audit found 1,005 duplicate candidates overall. Equivalent
candidates are consolidated and their distribution weights summed. All 505
spawnpoints, forced loot, poses, total spawn counts and per-item probabilities
are preserved. The export generator now merges repeated pool templates and
uses stable unique keys. The failure log includes the exception/stack and no
longer assumes the mod appearing in the stack caused the invalid data.

Client/addon builds and 95 Harmony target checks pass. The verifier checks all
505 loot pools for duplicate candidates, duplicate weights and missing
references, and verifies the installed Fika IsHeadless property. A comparison
against the previous data confirms all per-item weights and spawn settings.

Still required: a dedicated-host loading test and a raid using Lots of Loot.
The separately reported 2x supersampling ghosting/overexposure is unresolved:
no affected-client render capture or full log has been supplied. Do not treat
these headless/loot changes as a supersampling fix. Capture the affected client's
full BepInEx log, GPU/graphics settings, and a same-view 1x/2x comparison.

### Tripwire follow-up

The same initial raid log shows seven markers: four were skipped by the 50% roll,
and all three selected wires were skipped after a 30-second preload timeout and
missing CS-gas tripwire visuals. The planter now awaits General-priority local
pool preparation (the 4.1 Low-priority path does not await pool initialization),
cancels on raid teardown, and never plants against a timed-out/failed load.
It reports prolonged loading and the final planted count at normal log levels.

A valid M18/F-1 donor visual is cloned and retained for the raid. A map- and
template-scoped factory postfix repairs each pooled grenade instance returned to
native `SetupStakes`, replacing the fragile prefab-name asset scan. Borrowed
grenade objects are returned to their pools. The CS-gas payload and 50% spawn
chance remain unchanged. Verify visible wires and gas activation in a fresh
raid; for a deliberate all-marker test, temporarily set `TripwireChance = 1` on
all peers, then restore the desired chance afterward.

### Gameplay checks

The chain-door fuse now invokes SPT 4.1's native `HandlerExplosion` at the SZ-1
charge, using ManimalTerminal's `terminal_gates.json` blast settings: 5–10m
damage falloff, three fragments, strength 6, directional multiplier 7 over
360 degrees, and the authored blindness/contusion/armor settings and `Fire` FX.
The existing ten-second fuse, particles, audio and door animation remain.
The blast uses environmental damage without player attribution and runs on
each peer, as Fika's observed-player bridges reject remote environmental damage.
Duplicate plant events are ignored and the pending fuse is owned by GameWorld
so it is removed on raid teardown.

Test the chain door in solo and co-op with players exposed near the charge,
behind solid cover, and beyond the blast range. Check health, armor, concussion
and camera shake, including a guest planting the charge and a second raid.
Look for `[ChainDoor] native Terminal-style blast fired` on each peer. The build
and 94 Harmony target checks pass; these gameplay checks still require a raid.

1. Outdoors, compare unaimed view, low/high magnification and multiple scopes:
   snow and volumetric fog should remain visible. Check indoor fog exclusions,
   live fog toggles, weapon switching, and a second raid on another map.
2. Fight Wedge from cover and after breaking sight. He should search/reposition
   and acquire targets using his underlying brain. No IceWedge custom combat
   layers should be registered. Check his escorts also move normally.
3. Trigger T4 from the normal progression route. Look for `[T4Squad]` preparation
   and `active 1/5` through `active 5/5` log messages, including elapsed activation
   times. All five should arrive as one encounter at separated positions. If a
   whole-squad retry is reported, its reason is logged explicitly.

Compilation and Harmony target checks can verify integration contracts, but
rendering, NavMesh placement and combat require these in-game checks. Co-op is
also unverified; spawning/voice changes are gated to the bot authority.

### Quest progression and Boreas map unlock (September 7)

SPT 4.1 executes static routers in ascending priority and retains the last
response. The crossing filter ran before the core quest handler, which replaced
its filtered list. Both quest and map routes now run after core. Crossing gates
also filter QuestHelper results used by item-event quest deltas. Unaccepted
profile entries no longer bypass visit requirements; accepted/finished quests
are preserved so this update does not erase player progress.

The map response now clones the shared location data before applying a profile's
lock. The client refreshes the cached Icebreaker location flags when map selection
opens, using Boreas Part 3 (`9d5e3f7d6320a7fd139a2772`) with status Success. Having
the quest active or ready to hand in does not unlock the map.

The visit ledger retains the existing `db/icebreaker_visits.json` formats, writes
atomically and deduplicates raid ends per profile. Death, MIA and the other raid
outcomes count; aborted loads without Results and other maps do not. Completed
prerequisites are stamped before departure and during quest-list generation, so
old trips cannot satisfy a later BTR crossing. Runtime visit files are excluded
from release archives and Git. Keep the installed ledger when updating.

Regression verification uses synthetic profiles and the real SPT HTTP router,
serialization and Harmony bindings. It covers all eight gated quests, the six
first-visit openers, later BTR crossings, persistence, legacy data, retries,
per-profile isolation, quest deltas and Boreas quest states. Client/Fika builds
and all 96 client/addon patch targets pass. No live profiles were edited.

In-game acceptance check: use a fresh profile to confirm the entry and trader
openers are hidden; hand in Boreas Part 3 and reopen map selection without a
restart; end an Icebreaker raid (survival not required) and check trader offers.
Repeat for a Fika participant and verify subsequent BTR crossing gates. Already
accepted quests intentionally remain available after installing the update.

### Disabled map-lock regression

The initial progression update's menu refresh hid the map again for profiles
without Boreas Part 3, even when the server configuration disabled the lock.
The refresh now only grants a newly earned unlock and preserves access already
granted by the server. A missing `maplock.json`, a missing `finalQuestId` key, or
a null/blank value disables the server gate again. The shipped configuration
still requires Boreas Part 3. Malformed JSON logs a warning and falls back to
Boreas; restart the server and game after changing the configuration.

Regression checks cover all disabled-config forms, the actual server map response,
and execution of the built client refresh with server-unlocked, quest-locked,
newly-completed and unrelated-map inputs. All pass alongside the existing 96
patch-target checks. The map-lock hotfix contains only the client/server DLLs;
it deliberately preserves the installed configuration. No Fika addon change.

### Headless camera cleanup regression (September 10)

The supplied `log_2026.09.09_3-22-55_0.16.9.5.40743.zip` captures a headless
attempt, unlike the previously supplied guest log from a player-hosted attempt.
At 01:21:24 -04:00, `HeadlessGame.InitializeCameraAndUnloadAssets` calls
`CameraManager.SetCameraFromSettings`. Instantiating the camera throws empty
animation-curve errors in NightVision/ThermalVision and null-shader errors in
DistortCameraFX/GradingPostFX. Later errors include an already-completed async
task and missing visor assets. This archive does not contain BepInEx LogOutput
or Player.log, so it cannot establish the installed plugin versions or the
complete Fika initialization sequence.

Our earlier CanRender guard incorrectly disabled the scene-camera rejection
patch on headless. Rejection must run there too: it substitutes the native Cam2
prefab during Fika's camera cleanup, which still instantiates a camera despite
headless rendering being disabled. Removed this guard; optional camera grafts
and visual effects remain guarded. This patch now uses SPT's ModulePatch,
PatchPrefix and Enable lifecycle, with a non-bundled spt-reflection reference.

Build and regression verification pass, including all 96 patch targets and
construction/target resolution of the SPT wrapper. The client-only test hotfix
must be retested on headless; it fixes a demonstrated regression but does not
prove that the later task-completion error or the separate player-hosted hang
is resolved. No Fika addon code was changed.
