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
