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

Systems recovered this way for Icebreaker (scripts all in `analysis/`): doors +
keycard swipers, flares (+ material float sets), lootable containers, AI bake
(covers/voxels/patrols), spatial audio, weather assets, the scene list.

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
