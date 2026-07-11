# Manimal-AIDataDumper

Dev tool: dumps a raid's fully-restored AI point data to JSON so BSG's baked format
can be studied as ground truth for a custom-map point baker.

## What it captures

Right after `AICoversData.CachePoints()` finishes at raid load (the last step of the
covers restore chain in `BotsController.Init`), it serializes:

- **corePoints** — `AICorePoint` graph: id, connectionGroupId, position, connection ids
- **groupPoints / manualPoints** — every cover point with full field set (position,
  altPosition, firePosition, wallDirection, coverLevel, coverType, connectionGroup,
  corePointId, defenceLevel, way ids, ...)
- **ways / pathes** — the cover graph edges with navmesh path distances
- **voxelGrid** — grid min/max plus every serialized voxel cell (indices, position,
  haveNavMesh, closest point id, contained point/door/loot/exfil ids)
- **patrolsData** — container/simple loot points, exfiltration points
- **botZones** — zone flags, spawn point markers (sides/categories/infiltration),
  patrol ways with their points and core-point links
- **placeInfos** — AIPlaceInfo tactical zones (dark/mute/inside flags)

## Usage

Drop in `BepInEx/plugins/AIDataDumper/`, load a raid on any BSG map. Dumps land in
`BepInEx/plugins/AIDataDumper/dumps/<location>_<timestamp>.json`.

- `AutoDumpOnRaidLoad` (default on) — dump once per raid load
- `DumpHotkey` (default F9) — re-dump on demand mid-raid
- `GenerateHotkey` (default F10) — run the prototype cover scanner (below)
- `IndentJson` (default off) — pretty-print; files get ~3x bigger

Empty covers data (hideout, menus) is skipped automatically.

## Prototype cover scanner (F10)

Generates cover points from the live map's navmesh + collision geometry and dumps
them as `<location>_generated_<timestamp>.json` in the same shape as the real data,
so `analysis/analyze_dump.py` can diff generated vs BSG-baked on the same map.
Expect the game to freeze for several seconds while it scans.

Parity anchors: defence scoring calls the game's own `CoverPointDefenceInfo(Vector3)`
ctor, raycasts use `LayerMaskClass.HighPolyWithTerrainMask`, dedup uses the leaked
`CoverPointCreatorPreset` cluster constants, and conventions (wallDirection horizontal
unit, firePos = pos + 1.272 up, Stay/Sit only) come from ground-truth dumps.

Known v1 gaps vs BSG output: no corner-peek fire positions, no ambush/cover
neighborType classification, `alwaysGood`/`placeId` never set, voxel `HaveNavMesh`
is approximated by center sampling.

## Analysis

`python analysis/analyze_dump.py <dump.json> [more.json ...]` prints spacing/density,
field conventions, graph stats, and voxel invariants per dump.
