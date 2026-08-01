# Icebreaker_Sound retail parity audit (2026-07-30)

Full diff of retail 1.0's Icebreaker_Sound scene (level707) against our stack:
the SDK scene + the runtime restores (IcebreakerAcoustics spatial layer,
IcebreakerAmbientAudio ambient layer). Method: full typetree inventory of every
GameObject/component/AudioSource field on both sides, keyed by hierarchy path.

## Headline

- **GameObjects: 100% parity.** All 750 retail paths exist in our scene (947 GOs
  collapse to 750 unique paths — sibling name reuse). Our only extras are the 16
  authored `Icebreaker_Ambient/Amb_*` beds + 2 clip carriers, all deliberate.
- **AudioSource fields: parity everywhere it matters.** The only diffs are on the
  75 player-owned sources (clip/volume/playOnAwake/loop — our scene-baked fallback
  wiring, overwritten at runtime by the restored players) and were 2 accidental
  clips on the radio's SourceA/SourceB — now reverted (see fixes).
- **Components: 1214 of 1239 restored at full count** (see table in git history /
  this audit's scripts). Every ambient + spatial class is at exact retail count.

## Fixed during this audit

- `RadioSystem/RadioPlayer/SourceA+B` carried our room-tone bundle-carrier clips
  (they belong to the ship radio, not ambience). Reverted to clipless; the two
  clips now ride dedicated `ClipCarrier_*` objects under `Icebreaker_Ambient`,
  pointed at the real wavs (the `.audioclip` twins are 600-byte data-less
  AssetRipper stubs — never reference them).
- `SourceOccluder` (Occluded_sources, 3 sources) — was extracted but never
  rebuilt; now rides the ambient sidecar. Registers those sources for spatial
  occlusion processing.
- `GuidComponent` on SpatialAudioSystem — restored via sidecar.
- **Mixer routing**: retail routes 10 sources into mixer groups (day/night bed →
  AmbientOutDay/Night, wind+precip blender sources → Rain, radio → EventRadio).
  The scene's refs point into the ripped mixer asset (a dead bus — the cause of
  the silent outdoor bed). Runtime now resolves the same group names on the GAME
  master (BetterAudio.Master) and routes when found; BSG's DayTimeAmbientBlender
  drives those group faders natively.

## Remaining gaps (all layout-drifted between 1.0 and 4.0, or absent from 4.0)

| Component | Count | Why parked | Path to close |
|---|---|---|---|
| HandlerPlaySoundAdvanced | 12 | layout drift | `Door_blizzard` stingers (blizzard one-shot when deck doors open). Needs hand-layout of the drifted class + trigger-id plumbing to the door trigger entities. Most valuable remaining gap. |
| AmbientSoundBlender | 2 | layout drift | in/out ambient crossfade w/ high-pass. `OutdoorDuck` + env gating approximates the audible behavior. |
| EnvironmentSoundBlendSystem | 1 | layout drift | drives the blenders per-room (`RoomAmbientData.OutdoorAmbientVolume`). Same approximation. |
| RadioBroadcastController | 1 | layout drift | ship radio broadcast. `ClientBroadcastPlayer` parses clean (record kept in scratch `parity_extras.json`) but is useless without its controller. |
| ClientBroadcastPlayer | 1 | parked with controller | see above. |
| LocationScene | 1 | heavy drift (312/1896 bytes) | scene registry (loot/doors/zones lists), not audio. Map works without it. |
| MetaXRAcousticMap / ControlZone | 2 | class absent in 4.0 | Meta XR acoustics, new in 1.0. SpatialAudioSystem covers occlusion. |

## Known intentional deviations

- 75 player sources carry clip+playOnAwake in the scene as a fallback for when
  the runtime restore fails; the restore reconfigures them (BSG semantics:
  spread = Lerp(180,0,v), rolloff wrap = ClampForever, authored volumes).
- Retail's DaySource/NightSource ship `Mute: 1` (unmuted by mixer path); we
  unmute + voice by pinned TOD.
- Room tones scale by `ZoneToneVolume` (retail tames them on an unrestorable
  mixer bus; 1.0 raw is deafening).
- Wind/Precipitation blenders are restored + routed but dormant (their Init
  chain needs season/precip storage SOs; 4.0 ships
  `precipitationsoundstorageso.bundle` — possible future hookup).
