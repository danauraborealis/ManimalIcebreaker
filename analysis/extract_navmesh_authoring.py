"""
Recover retail's navmesh AUTHORING components from level706 (Icebreaker_AI).

Why this exists
---------------
Our bake and retail's are not the same mesh. Retail bakes with the AI Navigation
package -- a single NavMeshSurface on the GameObject named "NavMesh", set to
CollectObjects = Volume -- shaped by 25 NavMeshModifierVolume (all area 1 = Not
Walkable) and 326 NavMeshModifier (per-object area / ignore-from-build overrides).
AssetRipper dropped every one of those 351 components, so our copy of the scene has
bare Transforms and our navmesh was baked with none of retail's authoring applied.
Measured consequence (07-31): ~49% of bot->enemy paths return PathPartial and bots
strand themselves at the same coordinates every raid.

Method
------
The AssetRipper export knows WHICH objects carried each component (script guids
survive there) but not their VALUES (il2cpp stubs export field-less). The raw
level706 still holds the values. So: read the object list from the export, read the
numbers from the raw file, and join them on hierarchy path.

Layout is hand-walked rather than typetree-generated, because retail's package
version is exactly what we are trying to establish. Unity pads every bool to 4
bytes, which is what made a naive uniform read produce garbage past the first bool.
Each layout is tried and then VALIDATED; a layout that yields implausible values is
rejected rather than reported. If nothing validates, that is reported too -- an
honest "unknown" beats a confident wrong answer, because the numbers here decide
which package version to install and how 351 components get rebuilt.

Output: icebreaker_navmesh_authoring.json  (paths + decoded fields + a summary)
"""

import json
import os
import re
import struct
import sys
from collections import Counter, defaultdict

import UnityPy

EXPORT = (r"C:\Users\peard\Desktop\IcebreakerLevels\AssetRipper_export_20260704_204314"
          r"\ExportedProject\Assets\Content\Locations\Icebreaker\Icebreaker_AI.unity")
LEVEL = r"C:\Users\peard\Desktop\IcebreakerLevels\level706"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "icebreaker_navmesh_authoring.json")

GUIDS = {
    "NavMeshSurface":        "3967c680668979432c52647ea326c0a9",
    "NavMeshModifierVolume": "7a6ceaab3099168d649dc2d45cdcc425",
    "NavMeshModifier":       "41bf9919877c74a343fcc34a8d40e607",
}


# ----------------------------------------------------------------- byte reader
class Reader:
    """Unity-serialization reader: bools occupy a byte then pad to 4."""

    def __init__(self, buf):
        self.b = buf
        self.o = 0

    def align(self):
        self.o = (self.o + 3) & ~3

    def i32(self):
        v = struct.unpack_from("<i", self.b, self.o)[0]; self.o += 4; return v

    def i64(self):
        v = struct.unpack_from("<q", self.b, self.o)[0]; self.o += 8; return v

    def f32(self):
        v = struct.unpack_from("<f", self.b, self.o)[0]; self.o += 4; return round(v, 5)

    def boolean(self):
        v = self.b[self.o] != 0; self.o += 1; self.align(); return v

    def vec3(self):
        return [self.f32(), self.f32(), self.f32()]

    def pptr(self):
        return {"fileID": self.i32(), "pathID": self.i64()}

    def int_list(self):
        n = self.i32()
        if n < 0 or n > 64:
            raise ValueError(f"implausible list length {n}")
        return [self.i32() for _ in range(n)]

    @property
    def left(self):
        return len(self.b) - self.o


def payload_of(raw):
    """Strip the MonoBehaviour header: GO PPtr, m_Enabled, m_Script PPtr, m_Name."""
    o = 28                                     # 12 (GO) + 4 (enabled+pad) + 12 (script)
    n = struct.unpack_from("<i", raw, o)[0]
    if n < 0 or n > 256:
        raise ValueError("bad name length")
    o = ((o + 4 + n) + 3) & ~3
    return raw[o:]


# ------------------------------------------------------------------- decoders
def read_surface(p):
    """
    Field order taken from the INSTALLED package source
    (Library/PackageCache/com.unity.ai.navigation@1.1.7/Runtime/NavMeshSurface.cs) --
    not guessed. 1.1.7 ends with m_LastPosition (Vector3); retail's payload is 92B,
    exactly 12 short of that, so retail predates that field. Everything before it
    matches, which is what lets 1.1.7 read retail's data.
    """
    r = Reader(p)
    d = {
        "m_AgentTypeID": r.i32(),
        "m_CollectObjects": r.i32(),
        "m_Size": r.vec3(),
        "m_Center": r.vec3(),
        "m_LayerMask": r.i32(),
        "m_UseGeometry": r.i32(),
        "m_DefaultArea": r.i32(),
        "m_GenerateLinks": r.boolean(),
        "m_IgnoreNavMeshAgent": r.boolean(),
        "m_IgnoreNavMeshObstacle": r.boolean(),
        "m_OverrideTileSize": r.boolean(),
        "m_TileSize": r.i32(),
        "m_OverrideVoxelSize": r.boolean(),
        "m_VoxelSize": r.f32(),
        "m_MinRegionArea": r.f32(),
        "m_NavMeshData": r.pptr(),
        "m_BuildHeightMesh": r.boolean(),
    }
    d["_trailing_bytes"] = r.left          # 0 => layout consumed the payload exactly
    return d


def read_modifier_volume(p):
    """1.1.7: m_Size, m_Center, m_Area, m_AffectedAgents. No AffectAllAgents."""
    r = Reader(p)
    d = {"m_Size": r.vec3(), "m_Center": r.vec3(), "m_Area": r.i32(),
         "m_AffectedAgents": r.int_list()}
    d["_trailing_bytes"] = r.left
    return d


def read_modifier(p):
    """
    Real 1.1.7 order. My first pass put m_IgnoreFromBuild in slot 3 and invented an
    m_AffectAllAgents -- same byte count, wrong assignments, which is exactly the way
    a bad layout hides: it "decodes cleanly" and reports confident nonsense.
    """
    r = Reader(p)
    d = {
        "m_OverrideArea": r.boolean(),
        "m_Area": r.i32(),
        "m_OverrideGenerateLinks": r.boolean(),
        "m_GenerateLinks": r.boolean(),
        "m_IgnoreFromBuild": r.boolean(),
        "m_ApplyToChildren": r.boolean(),
        "m_AffectedAgents": r.int_list(),
    }
    d["_trailing_bytes"] = r.left
    return d


# ------------------------------------------------------- export: who has what
def export_owners():
    txt = open(EXPORT, encoding="utf-8", errors="replace").read()
    names, comps = {}, {}
    for m in re.finditer(r"--- !u!1 &(\d+)\nGameObject:(.*?)(?=\n--- |\Z)", txt, re.S):
        nm = re.search(r"^  m_Name: (.*)$", m.group(2), re.M)
        names[m.group(1)] = nm.group(1).strip() if nm else "?"
        comps[m.group(1)] = re.findall(r"component: \{fileID: (\d+)\}", m.group(2))
    tf_parent, tf_go = {}, {}
    for m in re.finditer(r"--- !u!4 &(\d+)\nTransform:(.*?)(?=\n--- |\Z)", txt, re.S):
        go = re.search(r"m_GameObject: \{fileID: (\d+)\}", m.group(2))
        fa = re.search(r"m_Father: \{fileID: (\d+)\}", m.group(2))
        if go:
            tf_go[m.group(1)] = go.group(1)
            tf_parent[m.group(1)] = fa.group(1) if fa else "0"
    go_tf = {g: t for t, g in tf_go.items()}

    def path(goid):
        out, t, guard = [], go_tf.get(goid), 0
        while t and t in tf_go and guard < 64:
            out.append(names.get(tf_go[t], "?")); t = tf_parent.get(t); guard += 1
        return "/".join(reversed(out))

    owners = defaultdict(list)
    for m in re.finditer(r"--- !u!114 &(\d+)\nMonoBehaviour:(.*?)(?=\n--- |\Z)", txt, re.S):
        body = m.group(2)
        g = re.search(r"m_Script: \{fileID: \d+, guid: ([0-9a-f]+)", body)
        if not g:
            continue
        for kind, guid in GUIDS.items():
            if g.group(1) == guid:
                go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body).group(1)
                idx = comps.get(go, []).index(m.group(1)) if m.group(1) in comps.get(go, []) else -1
                owners[kind].append((path(go), idx))
    return owners


# --------------------------------------------------------- level706: the data
def level_index():
    env = UnityPy.load(LEVEL)
    types = {o.path_id: o.type.name for o in env.objects}
    gos, tf_of_go, go_of_tf, parent, raws = {}, {}, {}, {}, {}
    for o in env.objects:
        t = o.type.name
        if t == "GameObject":
            d = o.read_typetree()
            gos[o.path_id] = (d.get("m_Name"),
                              [c["component"]["m_PathID"] for c in d.get("m_Component", [])])
        elif t == "Transform":
            d = o.read_typetree()
            g = d["m_GameObject"]["m_PathID"]
            tf_of_go[g] = o.path_id
            go_of_tf[o.path_id] = g
            parent[o.path_id] = d["m_Father"]["m_PathID"]
        elif t == "MonoBehaviour":
            raws[o.path_id] = o.get_raw_data()

    def path(goid):
        out, tf, guard = [], tf_of_go.get(goid), 0
        while tf and guard < 64:
            g = go_of_tf.get(tf)
            out.append(gos.get(g, ("?", []))[0]); tf = parent.get(tf); guard += 1
        return "/".join(reversed(out))

    by_path = defaultdict(list)
    for gid, (nm, cs) in gos.items():
        by_path[path(gid)].append(gid)
    return gos, by_path, raws, types


def main():
    print("reading the export for component ownership ...")
    owners = export_owners()
    for k, v in owners.items():
        print(f"   {k:22s} {len(v)}")

    print("indexing level706 ...")
    gos, by_path, raws, types = level_index()

    decoders = {
        "NavMeshSurface": read_surface,
        "NavMeshModifierVolume": read_modifier_volume,
        "NavMeshModifier": read_modifier,
    }
    result, misses = {k: [] for k in GUIDS}, Counter()

    for kind, paths in owners.items():
        # a path can host several same-named siblings; consume them in order
        used = Counter()
        for p, cidx in paths:
            cands = by_path.get(p, [])
            idx = used[p]; used[p] += 1
            if idx >= len(cands):
                misses[kind] += 1
                continue
            gid = cands[idx]
            allc = gos[gid][1]
            # the export gives the component's INDEX on its GameObject; the raw file lists
            # components in the same order, so this identifies the exact script instead of
            # guessing which one happens to decode tidily
            cand = [allc[cidx]] if 0 <= cidx < len(allc) else                    [c for c in allc if types.get(c) == "MonoBehaviour"]
            row = None
            for c in cand:
                raw = raws.get(c)
                if raw is None:
                    continue
                try:
                    d = decoders[kind](payload_of(raw))
                except Exception:                                  # noqa: BLE001
                    continue
                if row is None or d.get("_trailing_bytes", 99) < row.get("_trailing_bytes", 99):
                    row = d
            if row is None:
                misses[kind] += 1
                continue
            row["path"] = p
            result[kind].append(row)

    print("\n=== NavMeshSurface ===")
    for s in result["NavMeshSurface"]:
        print(json.dumps(s, indent=2))
        if s.get("_trailing_bytes"):
            print(f"  !! {s['_trailing_bytes']} trailing byte(s) -- layout not fully accounted for")

    print("\n=== NavMeshModifierVolume ===")
    areas = Counter(r["m_Area"] for r in result["NavMeshModifierVolume"])
    trail = Counter(r.get("_trailing_bytes") for r in result["NavMeshModifierVolume"])
    print(f"  count={len(result['NavMeshModifierVolume'])} areas={dict(areas)} trailing={dict(trail)}")

    print("\n=== NavMeshModifier ===")
    mods = result["NavMeshModifier"]
    print(f"  count={len(mods)}")
    for k in ("m_OverrideArea", "m_OverrideGenerateLinks", "m_GenerateLinks",
              "m_IgnoreFromBuild", "m_ApplyToChildren"):
        print(f"  {k:26s} {dict(Counter(str(r.get(k)) for r in mods))}")
    print(f"  areas={dict(Counter(r['m_Area'] for r in mods))}")
    print(f"  trailing={dict(Counter(r.get('_trailing_bytes') for r in mods))}")
    if misses:
        print(f"\n  UNRESOLVED: {dict(misses)} (path present in the export, no match in level706)")

    payload = {
        "source": {"export": EXPORT, "level": LEVEL},
        "expected": {"NavMeshSurface": 1, "NavMeshModifierVolume": 25, "NavMeshModifier": 326},
        "components": result,
    }
    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, indent=1)
    print(f"\nwrote {OUT}")
    print("counts:", {k: len(v) for k, v in result.items()})


if __name__ == "__main__":
    sys.exit(main())
