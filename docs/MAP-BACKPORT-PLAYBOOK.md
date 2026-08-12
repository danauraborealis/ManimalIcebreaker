# Retail → SPT Map Backport Playbook

A guide for backporting a retail EFT map into SPT with an AI coding assistant
(Claude or similar) doing the heavy lifting. Distilled from the Icebreaker backport
(retail 1.0 → SPT 4.0). The **ManimalIcebreaker repo is the working reference
implementation** — nearly every step below points at real code in it, so keep a
copy next to your working folder and let the assistant read it instead of
reinventing things.

---

## For the human: how to use this document

1. **Start every session by giving the assistant this file** (paste it or point at
   it) plus the locations of: your SPT install, your copy of the ManimalIcebreaker
   repo, your Unity SDK project, and the retail game files. It cannot guess paths.
2. **Work one phase at a time, in order.** Say "we're on Phase 3, doors" — don't
   ask for everything at once. Run a raid after each phase before adding the next.
3. **Paste errors and logs verbatim** (server console, `BepInEx\LogOutput.log`).
   Screenshots of in-game weirdness help. "It didn't work" is not debuggable;
   a log line usually is.
4. **Make the assistant verify instead of assume.** Good prompts: "check that
   against the actual file", "confirm that item id by its locale name", "prove
   which file the game loaded by timestamps". The trap list at the bottom exists
   because assumptions burned days.
5. When something breaks, ask the assistant to **add a diagnostic and read the log**
   before letting it theorize. One raid with real numbers beats five guesses.

**What you need installed/available:**
- A retail EFT install of the version the map ships in (you only read files from it)
- A working SPT dev install (client + server), same Unity version as the retail build
- The WTT map SDK Unity project (matching Unity editor version, e.g. 2022.3.43f2)
- Python with `UnityPy` (and its TypeTreeGenerator), plus .NET SDK for the mods
- The ManimalIcebreaker source repo: `icebreaker-client/` (BepInEx plugin),
  `icebreaker-server/` (server mod), `analysis/` (all extraction/generator scripts)
- Any content mods your map depends on for bosses/items — decide this early;
  the server crashes on loot tpls it can't resolve

**SPT install layout** (matters for deploys and packaging): the game root contains
`BepInEx\`, `EscapeFromTarkov_Data\`, and `SPT\` — and `user\mods\` lives INSIDE
`SPT\`, not at the root.

---

## Phase 0 — Feasibility

1. **Unity version match**: the retail build's Unity version must equal the SPT
   client's. Check the level file header or `globalgamemanagers`.
2. **Scene delivery format**: EFT maps are BUILT-IN PLAYER SCENES — numbered
   `level###` files (+ `.resS` siblings) in retail `EscapeFromTarkov_Data\`, NOT
   asset bundles. No bundle-metadata encryption gate applies to them.
3. **Host location slot**: SPT's `Locations` record is closed to new ids. Pick a
   dormant shipped stub (Icebreaker rides **Suburbs**) — every native lookup
   (GetLocation, botgen, insurance, scav-time) then resolves with zero patching.
   Rebrand the map name via a locale transformer, not Base.Name.

## Phase 1 — Locate and stage the scenes

1. Run `analysis/extract_scene_list.py "<retail EscapeFromTarkov_Data>"` — it reads
   BuildSettings from `globalgamemanagers`; scene index == level file number, and
   it groups by map folder to print each map's level range.
2. Copy that map's `level###` (+`.resS`) files plus `globalgamemanagers` and
   `globalgamemanagers.assets` into a staging folder. All extraction scripts load
   `UnityPy.load(globalgamemanagers.assets, level###)`.
3. Note which scene is which: maps have a dedicated `_AI` scene, a Scripts scene
   (must be FIRST in the scene preset), plus design/lighting/sound scenes.
4. Assets referenced from scenes but stored globally (materials, textures) live in
   retail `sharedassets#.assets` / `resources.assets` — pull ad hoc when a system
   needs them (Icebreaker's flare materials came from sharedassets3).

## Phase 2 — AssetRipper export → SDK import

1. AssetRipper the staged files into an ExportedProject; import the scenes into
   the SDK Unity project.
2. EXPECT the rip to be lossy in specific, fixable ways:
   - **EFT MonoBehaviours become empty husks** (components with no fields, or
     missing scripts). Doors, lootable containers, flares, weather, audio — all
     need raw recovery (Phase 3) + editor rebake (Phase 4).
   - **Lighting data is lost** → interior lamps serialize at intensity 0 (fix via
     runtime lamp revival in the client plugin, or lighting-data reassignment in
     the editor).
   - **Static flags/batching** cause "collider moves but mesh doesn't" on anything
     animated (doors, props) → an unstatic pass fixes (see the repo's 1U tool).
   - Occasional corrupted transforms on odd objects — spot-fix as found.

## Phase 3 — Raw component recovery (UnityPy + TypeTreeGenerator)

The pattern (see `analysis/extract_doors.py` — the canonical implementation):
1. `TypeTreeGenerator("<unity version>")` + `load_local_dll_folder(<SPT Managed>)`
   gives CURRENT-version typetrees; the retail serialized data usually DRIFTS from
   them (fields added/removed between versions).
2. Read MonoBehaviours with `read(check_read=False)`; parse against a hand-edited
   flat tree (`get_nodes_up` → flatten → surgery → rebuild → `read_typetree`).
3. Example drift found on the WorldInteractiveObject family (1.0 vs 0.16.9): three
   fields replaced by four plain values, plus variable-size tails appended per
   class. Your version pair will have its own drift — hexdump one object and
   hand-walk it when the parse goes to garbage.
4. **When needed fields land AFTER a variable tail** (misaligned), anchor-carve:
   find a recognizable byte pattern (e.g. a 24-hex item tpl with its length prefix)
   and struct-walk from there (`analysis/extract_lootables.py::carve_lc_tail`).
5. ALWAYS build sanity checks into the extraction (tpl regex, non-empty ids) so
   drift screams instead of producing silent garbage.
6. Record hierarchy paths with the `name~k` sibling-ordinal scheme + composed
   world position — the editor rebake matches on path first, position second.

7. **The AssetRipper export tells you WHICH objects had a component; the raw level
   still holds its VALUES.** For IL2CPP scripts AssetRipper writes a field-less
   stub `.cs` and therefore dumps the MonoBehaviour with an empty body — the
   hierarchy and the `m_Script` guid survive, the tuning does not. So use the
   export to find the carriers (grep the script's `.meta` guid across the scene
   YAML) and the raw `levelNNN` to recover the numbers. Identify the class in the
   raw file WITHOUT a typetree by grouping every MonoBehaviour on its `m_Script`
   PPtr and matching group size to the count the export gave you — an exact
   count match pins the class, then the payload is a fixed-size struct you can
   `struct.unpack` (header is GO PPtr 12B, enabled 1B→align 4, script PPtr 12B,
   name string→align 4). Validate by range-checking every field against the
   4.0 class's `[Range]` attributes and defaults; if the whole set lands on
   plausible values in declaration order, the layout did NOT drift.
   Done for `VolumetricLight` (49 lights, July 2026) — 1.0 and 4.0 matched
   byte-for-byte, so the authored values transferred with no surgery at all.
8. Before restoring a component at runtime, grep the assembly for who ELSE caches
   it (`GetComponent<X>()` stored to a field, or collected into a list at init).
   Anything that cached it before you existed needs rebinding by reflection, or
   your restored component silently ignores the system that's supposed to drive
   it — `VolumetricLight` is cached by both `CullingLightObject.volumetricLight_0`
   (distance-fade intensity re-check) and `LampController.list_2` (on/off + dim).

Systems recovered this way for Icebreaker (scripts all in `analysis/`): doors +
keycard swipers, flares (+ material float sets), lootable containers, AI bake
(covers/voxels/patrols), spatial audio, weather assets, volumetric lights, the
scene list.

## Phase 4 — Editor rebake (the "Author" scripts)

Pattern per system (`IcebreakerTools/Editor/IcebreakerRetailDoors.cs` in the SDK
project is canonical): strip existing components → index all scene transforms by
`name~k` path → per extracted row: path match, reject if >0.5m from the recorded
world position, fall back to nearest same-named transform within 1m →
AddComponent(stub type) → assign fields via SerializedObject.

Critical supporting facts:
- **Stub scripts must be real MonoBehaviours with the game's serialized field
  NAMES** (the bundle binds to the game's class by assembly+namespace+class name;
  fields bind by name). Ripped stubs often aren't even MonoBehaviours — rebuild
  them from the game's typetree (dump the field list with TypeTreeGenerator).
- Unity only shows a MonoBehaviour in Add Component if the class name matches its
  file name — one class per file for authoring components.
- **Unstatic pass**: strip static flags from anything that moves; enforce the
  retail `_SHADOW_` mesh split (proxy meshes ShadowsOnly, visual twins cast Off).
- Wire the `LocationScene` component's arrays (LootableContainers,
  WorldInteractiveObjects, ...) — the game enumerates scene content through them.
- **SAVE ALL SCENES before building the bundle** — the bundle build reads scene
  files from disk; unsaved inspector-visible components silently don't ship.

## Phase 5 — Bundles and loading

1. Build ONE scene bundle containing all the map's scenes (shared assets dedupe
   inside it; no cross-bundle dependencies), plus a small preset bundle whose
   ScenesPreset lists (scene bundle key, scene name) pairs — Scripts scene first.
2. **THE BIG ONE**: the client resolves bundle keys against
   `EscapeFromTarkov_Data\StreamingAssets\Windows\` FIRST, and only uses SPT's
   mod-bundle system for keys with no local file. Deploy the scene + preset
   bundles THERE. Small custom bundles (UI prefabs, props) with mod-only keys load
   through the server mod's `bundles.json` fine. A stale StreamingAssets copy
   looks exactly like "my rebuild changed nothing" — check file timestamps before
   debugging content.
3. A distribution zip should mirror the install root so it's drag-and-drop:
   `BepInEx/plugins/<YourMod>/...`, `EscapeFromTarkov_Data/StreamingAssets/...`,
   `SPT/user/mods/<YourMod>/...`.

## Phase 6 — Server mod (C#, SPT 4.x)

Reference: `icebreaker-server/IcebreakerMod.cs`.
1. An `IOnLoad` service (priority after PostDBModLoader) loads your `db/base.json`
   into the dormant slot's `.Base`, locale-renames the slot, and clones a real
   map's ScavRaidTimeSettings entry.
2. **base.json**: merge the retail map's (spawns, hostility, weather, exits) into
   the SPT-shaped stub. Gotchas: remap retail boss roles to whatever mod roles you
   actually have installed; the game LOWERCASES the player's entry point before
   matching — exits' `EligibleEntryPoints` must be lowercase; keep the stub's Id.
3. **Loot properties are LazyLoad<T>-wrapped**: deserialize the INNER model type
   and wrap it in `new LazyLoad<T>(() => value)`. Deserializing into `LazyLoad<T>`
   directly fails SILENTLY, and a mixed-source fallback then crashes raid start
   with a dictionary KeyNotFound. Discover model types with the compile-error
   probe trick (`int a = location.StaticLoot;` — the CS0029 error names the type).
4. **staticContainers.json must be generated from the BUILT BUNDLE, not from the
   retail extraction** — the SDK regenerates container Ids on every rebake. Scan
   the bundle with UnityPy for LootableContainer ids/templates, then generate
   (see `analysis/gen_static_containers.py`). REDO after every bundle rebuild.
5. **staticLoot.json** (per-container-type loot pools): generate your own file
   (borrow a suitable map's pools, bake additions in — `analysis/gen_static_loot.py`).
   Never mutate another map's in-memory tables — they're shared references and
   your edits leak into that map's raids. VERIFY every container tpl by its locale
   name before mapping pools — several "obvious" ids are wrong (the medcase-looking
   id is the Toolbox; the safe-looking id is the Jacket).
6. **looseLoot.json**: retail loose loot is generated server-side per raid —
   UNRECOVERABLE from game files. Author your own: marker components in the SDK
   (pool / probability / specific-item override / group name), an editor export,
   and a generator (`analysis/gen_loose_loot.py`). Groups become
   IsGroupPosition/GroupPositions (game picks one position per raid); override
   spots go into `spawnpointsForced` (probability 1 = guaranteed). Beware
   QuestItem twin items — same name, different id; check `QuestItem: false`.
7. `AllExtracts = []` is fine for a PMC-only first release; StaticAmmo can borrow
   a real map's.

## Phase 7 — Client plugin systems (rebuild only what the rip lost)

Icebreaker's set, in rough bring-up order (all in `icebreaker-client/`): lamp
revival + ambient fill; weather resurrection (sidecar-driven); acoustics (spatial
audio bake + indoor zone tones); occlusion culling (self-hosted Perfect Culling
runtime compiled WITHOUT UNITY_EDITOR — the editor assembly's layout won't bind;
per-frame apply budget, never-cull whitelist for long-sightline decor); flares;
baked AI data loading with generated fallback; retail door links; event-driven
spawn system (trigger sidecar → batched spawns at distinct markers); intro
cutscene (RenderTexture + OnGUI; AudioSource priority 0; volume via the game's
Overall slider dB table); special exfils (for a timed heli, the TELEPORT LOCK
trick — move the exfil zone 1km down and teleport it back when it should arm —
beats fighting the availability system with patches); keypad/passcode doors.

Keep every data sidecar next to the plugin DLL and load paths relative to
`Assembly.GetExecutingAssembly().Location` — the folder stays relocatable.

## Trap list (each of these cost real time — have your assistant check them)

- **AssetRipper script GUIDs are per-run, NOT deterministic.** Importing a second
  map's export into an SDK whose stubs came from an earlier export means every
  script reference lands on Missing Script. Fix is mechanical: index class-name →
  guid on both sides and rewrite the imported YAML (scenes AND .playable/.prefab
  assets). Do the same against Library/PackageCache for Unity package classes
  (Timeline, uGUI, TMP) — AssetRipper decompiles its own copies of those too.
- StreamingAssets-first bundle resolution (Phase 5.2).
- Container Ids regenerate on every SDK rebake (Phase 6.4).
- LazyLoad<T> silent deserialization failure (Phase 6.3).
- Container tpls guessed instead of locale-verified (Phase 6.5).
- QuestItem twin items in loot pools.
- Unsaved scenes → stale bundle (Phase 4).
- Build-script deploys that fail silently (MSBuild Copy with ContinueOnError;
  scripted find/replace that didn't match) — always verify the timestamp moved /
  the file actually changed.
- Harmony: one AmbiguousMatchException kills the whole patch-attach batch —
  isolate per-patch try/catch; resolve ambiguous overloads explicitly.
- Group bot-spawn APIs that take a count can silently yield ONE bot — prepare
  singles and burst-activate.
- Pausing a bot's patrol layer doesn't pause combat/search — heard gunfire moves
  "held" bots.
- Navmesh islands: spawn markers on decks with no bot-walkable route (EFT has no
  climbable ladders) freeze force-moved bots in place — filter markers by
  reachable height; allow partial paths.
- `LocationScene.GetAllObjects<T>` returns EMPTY (not an error) for types missing
  from its hardcoded cache list.
- Moved collider + frozen mesh = static batching.
- The game assemblies define a `Paths` type that shadows `BepInEx.Paths`.
- Occlusion culling budget: too many toggles per frame = interior freeze (350/frame
  was smooth where 1500 froze).
- Layered AI "improvements" fight each other — Icebreaker ultimately REMOVED all
  custom brain modifications and kept only spawn placement. Prefer vanilla brains
  + good spawn positions over mind rewires.

## Verification discipline

- Server log (`SPT\user\logs\spt\`) for loot generation counts and mod init lines;
  client log (`BepInEx\LogOutput.log`) for binding/culling/AI diagnostics.
- When something misbehaves, ship a temporary diagnostic that prints real numbers
  (e.g. container count / enabled / layer / item-bound; bot release positions and
  straggler distances). One raid of numbers repeatedly solved what days of
  theorizing didn't.
- Run a raid after every phase. Never stack two untested systems.

## Shader stand-in forensics (learned on the deck VP shader, July 2026)

When a BSG shader can't be bound at runtime and needs an SDK stand-in:
1. **Read the real shader's property STRINGS before writing any math** — dump it from
   the game's `shaders` bundle via UnityPy's ShaderConverter. Even without DXBC
   disassembly, BSG's property annotations document the conventions (e.g.
   `_MainTex0 ("Base (RGB) Smoothness (A)")` = per-pixel gloss lives in diffuse
   alpha × the scalar — a flat scalar reads glossy-wrong).
2. **Vertex colors are the blend driver and they're fragile**: `PlayerSettings ->
   Optimize Mesh Data (StripUnusedMeshComponents)` strips COLOR from static-batch
   combines when the stand-in reads color via a custom vertex function (usage
   analysis blind spot). Turn it OFF and RESTART the editor before building (the
   build uses the in-memory value).
3. Surface-shader gotchas: `uv_TexName` in Input auto-declares `_TexName_ST`
   (redeclaration error), but that auto-declaration is NOT referenceable from user
   code either — route raw UVs through a texture with identity ST instead.
4. **Transparent/forward stand-ins flicker in EFT's deferred world** (per-object
   forward light lists churn, worsened by any light-toggling system). Decal-type
   stand-ins should be ambient-SH-lit (stable) instead of realtime-lit.
5. Verify texture ALPHA survived the rip before trusting alpha-driven features
   (precedent: melt-dissolve _MainTex, then the smoothness maps).
6. **Vertex animation must displace along a UNIFORM direction, never the vertex
   normal.** Meshes duplicate vertices at every UV island and hard edge with
   different normals; normal-based displacement pulls the duplicates apart and
   the mesh visibly rips at the seams (cloth stand-in, arms in the cutscene,
   July 2026). Position-seeded noise + one shared direction keeps coincident
   duplicates welded by construction.
7. **Before iterating on a stand-in shader, verify it's the shader that
   actually renders.** The runtime name-rebind swaps materials onto the game's
   shader whenever one with that name exists in ANY loaded location — and the
   client's own EscapeFromTarkov_Data/sharedassets*.assets is one the
   global-bundle check misses entirely (4.0 ships Cloth/ClothShader +
   _backface compiled in sharedassets5, same home as retail; property lists
   match the 1.0 materials). Four cloth stand-in fixes produced byte-identical
   "arm rips" because none of them ever rendered in a raid; the log's
   `[RebindShaders] Nx <name>` line is the ground truth. Once rebound, the only
   fixes that reach the screen live in the material/texture data the rebind
   preserves — the actual bug was DXT1 quantizing the cutout mask's whites to
   250-254 under an authored exact threshold of 1.0 (source-PNG analysis shows
   clean 255s; the BUNDLE texture is what the shader samples). Fix: import the
   mask uncompressed. Identical-result fixes exclude the mechanism you touched;
   the fourth identical result should have pointed at the render path itself.

## Performance playbook (learned on Icebreaker, Aug 2026 — read BEFORE optimizing)

The dense-view fps hunt burned a week. The final map of what matters, so the next
map skips straight to the answer:

- **Measure before touching anything.** Port the `[FrameSplit]` probe first
  (RaidFixPatches: Camera.onPreCull..onPostRender bracket + WaitForEndOfFrame
  marker; FrameTimingManager returns NOTHING in this client build). It splits every
  frame into scripts / camMain / presentTail. camMain-bound = draw submission
  (LOD/cull territory); scripts-bound = AI (bot count); presentTail = GPU.
  Also port `[QualityCensus]` (one-shot QualitySettings dump) — we A/B'd shadow
  values the game was already running and learned nothing for a whole round.
- **LOD bias is THE lever on ripped maps.** EFT's Object LOD slider hard-clamps to
  [2,4] (validator in GraphicsSettingsClass; slider "2" = Unity lodBias 2.0 —
  verified 1:1). BSG authors LODGroup CULL thresholds assuming bias >= 2, and most
  map LODGroups are 1-LOD (Icebreaker: 79.8k of 81.8k; Customs: 50k of 67k) — they
  are a CULL system, not a detail system. Sub-2 bias = everything culls closer =
  the fps win, but near-field props visibly pop. Fix: cell-tiered cull floors
  (IcebreakerLodCullFloor.cs) — protective floor near the camera, aggressive floor
  beyond, indoor/outdoor radius split via EnvironmentManager. Shipped defaults:
  bias 0.7, far 0.1, near 0.006, radii 27.1/19.49m.
- **Lamps are realtime deferred lights** (the rip loses baked lighting) and BSG's
  CullingLightObject system FADES intensity (authored 50→80m window) but never
  disables a light — GPU-bound players bleed here even when the dev box (GPU idle)
  shows nothing. Clamp the fade window (`_fadeStartDistance`/`_fadeEndDistance` +
  `method_3()` recompute) — shipped default 25m — and force-restore during any
  cutscene wide shots (CutsceneShowAll already does). LampIntensity 0 is the
  nuclear option and the map stays readable (emissives/flares carry the look).
- **Exonerated — do not re-litigate without new evidence:** occlusion culling
  (retail's own 84k-cell bake made ZERO difference — dense views are open
  sightlines), runtime static batching (bundles ship ~91% pre-batched at build
  time), shadows (rip loses baked lighting; barely any shadow work exists),
  pixel light count (map renders deferred), far-plane/visibility (nothing but
  ocean past the map), and BSG's cell autocull + area-light instancing (both are
  post-0.16 engine work — dormant/absent in SPT's client, confirmed on a live
  Customs census; nothing to feed data to).
- **Verify a lever ENGAGES before trusting its A/B.** Three separate knobs this
  week were inert when first tested (LODGroup.enabled toggling, the first
  LightCullDistance, CellCull) — a "no effect" result on a knob that never fired
  is measuring nothing. Every clamp should log what it changed FROM.

## Spawning and third-party coexistence (Aug 2026)

- **Deterministic spawn tables, never count-and-correct.** The rogue flood was
  chance-rolled BossLocationSpawn rows + client double-fill; the fix was exact
  rows at 100%, and DELETING the culler machinery, not tuning it.
- **Native botEvent waves work** (BossSpawnScenario.smethod_0 prefix appends rows;
  TriggerName="botEvent" + TriggerId; MoreBots prepatcher makes custom role enums
  parse) but the native pipeline has no naked-profile guards and tears bots under
  load (invisible gear-shell mannequins) — keep a force-spawner as default and a
  mannequin sweeper (never-activated bot for 30s → despawn) regardless.
- **Third-party mods WILL break on an unknown map id.** The choke-point firewall
  pattern (wrap our critical path, name the culprit, degrade not die) plus targeted
  shims: PBS/APBS needs the map masqueraded as 'laboratory' INSIDE the bot
  generator call (their router hook stomps it back later), and the shim must be
  LOUD when it can't find its hook point — a silent no-op cost a player every
  vanilla bot for days. Add a one-time proof-of-life log to any DI override so
  "did our generator even run" is a grep.
- **Voice muting for held/ambush bots**: BotTalk.CanSay only gates vanilla lines —
  SAIN speaks through Player.Speaker directly. Gate PhraseSpeakerClass.Play/Queue
  (the terminal sink) for held bots.
- **Audio sources from restores bypass the game's volume sliders** — EFT applies
  volume via mixer channels, never AudioListener. Adopt every null-group
  AudioSource into BetterAudio.MasterMixerGroup on a 10s sweep.

## Feeding a new project this project's memory

The assistant's persistent memories for Icebreaker live as plain markdown at
`C:\Users\peard\.claude\projects\C--Users-peard-Desktop-megagugged-spt-hideoutcat-main\memory\`
(index: MEMORY.md). They are keyed to the session's working directory — a session
started in a new project folder will NOT auto-load them. To transfer: point the
new session at that folder (it can read the files directly if given the path), or
copy the relevant .md files into the new project's docs. The highest-value ones
for a new backport: map-backport-playbook (pointer), eft-bot-ai-and-map-data,
eft-navmesh-and-waypoints, icebreaker-native-culling, icebreaker-fps-lodbias,
icebreaker-audio-parity, icebreaker-loot-pipeline, fika-compat-audit,
choke-point-firewalls.
