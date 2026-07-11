# recovers BSG's spatial-audio + environment-trigger authoring from the retail icebreaker
# scene binaries. AssetRipper exported these MonoBehaviours as hollow shells (no type
# trees in the build), but the raw serialized bytes are intact in the level files —
# UnityPy + typetrees generated from SPT 4.0's Mono Assembly-CSharp decode them.
#
# retail 1.0 vs 0.16.9 layout drift (hand-verified against hexdumps):
#   SpatialAudioRoom: retail inserts ONE field after _bounds — an array of per-Area
#     TRS boxes {Vector3 pos, Quaternion rot, Vector3 size}. rest is identical.
#   BaseSpatialAudioPortal: retail stopped serializing `portalType` AND `state`
#     (verified: exactly two 0/1-valued 4-byte slots between portalName and the
#     collider PPtr across all 207 portals — FitToGeometry + AutoRotate).
# node surgery below applies both fixes to the generated trees.
#
# output: icebreaker_spatial_audio.json — consumed at raid load by the client mod:
#   SpatialAudioRoom / SpatialAudioPortal / UniversalTriggerSpatialAudioPortal /
#   AudioTriggerArea / SpatialAudioSystem (Icebreaker_Sound)
#   IndoorTrigger / TriggerGroup / EnvironmentManager (Icebreaker_Scripts, tier-2 env)
#   plus BSG's ambient layer (SoundPoint/LoopAmbientSoundPlayer/...) for later use.
# refs are emitted as scene path_ids + a global path_id -> GameObject-path table.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

TTH.read_typetree_boost = None  # boost reader chokes on the surgically-patched nodes

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_spatial_audio.json"

SCENES = {
    "Icebreaker_Sound": LEVELS_DIR / "level707",
    "Icebreaker_Scripts": LEVELS_DIR / "level698",
}

# class -> needs-portal-surgery (BaseSpatialAudioPortal descendants)
PORTAL_CLASSES = {"SpatialAudioPortal", "UniversalTriggerSpatialAudioPortal", "MultiWindowPortal"}
CORE = {
    "SpatialAudioRoom", "AudioTriggerArea", "SpatialAudioSystem",
    "IndoorTrigger", "TriggerGroup", "EnvironmentManager",
} | PORTAL_CLASSES
# BSG's ambient layer — recovered opportunistically, drift not hand-verified (flagged per-row)
BONUS = {
    "SoundPoint", "SoundPointsManager", "AmbientSoundPlayer", "LoopAmbientSoundPlayer",
    "AmbientSoundPlayerGroup", "AmbientAudioSystem", "EnvironmentSoundBlendSystem",
    "SourceOccluder", "SpatialAudioCrossSceneGroup",
}
TARGETS = CORE | BONUS


def flat(nodes):
    """TypeTreeNode tree -> flat [(level, type, name, metaflag)] list."""
    out = []

    def walk(n, lvl):
        out.append([lvl, n.m_Type, n.m_Name, n.m_MetaFlag or 0])
        for c in n.m_Children or []:
            walk(c, lvl + 1)

    walk(nodes, 0)
    return out


def to_tree(flat_list):
    return TypeTreeNode.from_list(
        [TypeTreeNode(l, t, n, 0, 0, m_MetaFlag=m) for l, t, n, m in flat_list]
    )


def drop_field(flat_list, name, level=1):
    """remove a field node and its subtree."""
    out, skip = [], False
    for row in flat_list:
        if skip:
            if row[0] > level:
                continue
            skip = False
        if row[0] == level and row[2] == name:
            skip = True
            continue
        out.append(row)
    return out


def insert_after_field(flat_list, after_name, sub, level=1):
    """insert a flat subtree right after the named field's subtree ends."""
    out, i = [], 0
    n = len(flat_list)
    while i < n:
        row = flat_list[i]
        out.append(row)
        if row[0] == level and row[2] == after_name:
            i += 1
            while i < n and flat_list[i][0] > level:
                out.append(flat_list[i])
                i += 1
            out.extend(sub)
            continue
        i += 1
    return out


# retail's per-Area TRS box array (name is ours; layout is what the bytes say)
AREA_BOX_SUB = [
    [1, "vector", "areaBoxes", 0],
    [2, "Array", "Array", 0x4000],
    [3, "int", "size", 0],
    [3, "AreaBox", "data", 0],
    [4, "Vector3f", "pos", 0],
    [5, "float", "x", 0], [5, "float", "y", 0], [5, "float", "z", 0],
    [4, "Quaternionf", "rot", 0],
    [5, "float", "x", 0], [5, "float", "y", 0], [5, "float", "z", 0], [5, "float", "w", 0],
    [4, "Vector3f", "size", 0],
    [5, "float", "x", 0], [5, "float", "y", 0], [5, "float", "z", 0],
]


def patched_nodes(gen, asm, fullname, cls):
    nodes = gen.get_nodes_up(asm, fullname)
    fl = flat(nodes)
    if cls == "SpatialAudioRoom":
        fl = insert_after_field(fl, "_bounds", AREA_BOX_SUB)
    if cls in PORTAL_CLASSES:
        fl = drop_field(fl, "state")
        fl = drop_field(fl, "portalType")
    return to_tree(fl)


def sanitize(v):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            return {"ref": v["m_PathID"], "file": v["m_FileID"]} if v["m_FileID"] else {"ref": v["m_PathID"]}
        return {k: sanitize(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if isinstance(v, bytes):
        return v.decode("utf-8", "replace")
    if hasattr(v, "__dict__"):
        return {k: sanitize(x) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def extract_scene(env, level_name, gen):
    sf = next(f for k, f in env.files.items() if str(k).endswith(level_name))
    gos, transforms = {}, {}
    comp_to_go = {}  # ANY component path_id -> owning GO path_id (colliders included)
    for obj in sf.objects.values():
        t = obj.type.name
        try:
            if t == "GameObject":
                gos[obj.path_id] = obj.read()
            elif t in ("Transform", "RectTransform"):
                transforms[obj.path_id] = obj.read()
        except Exception:
            pass

    def path_of(go):
        try:
            tr = None
            for comp in go.m_Component:
                c = comp.component if hasattr(comp, "component") else comp[1]
                if c.path_id in transforms:
                    tr = transforms[c.path_id]
                    break
            parts = [go.m_Name]
            hops = 0
            while tr is not None and hops < 64:
                hops += 1
                father = tr.m_Father
                if father.path_id == 0 or father.path_id not in transforms:
                    break
                tr = transforms[father.path_id]
                pgo = gos.get(tr.m_GameObject.path_id)
                parts.append(pgo.m_Name if pgo else "?")
            return "/".join(reversed(parts))
        except Exception:
            return go.m_Name

    def trs_of(go):
        try:
            for comp in go.m_Component:
                c = comp.component if hasattr(comp, "component") else comp[1]
                if c.path_id in transforms:
                    t = transforms[c.path_id]
                    p, r, s = t.m_LocalPosition, t.m_LocalRotation, t.m_LocalScale
                    return {"pos": [p.x, p.y, p.z], "rot": [r.x, r.y, r.z, r.w],
                            "scale": [s.x, s.y, s.z]}
        except Exception:
            pass
        return None

    def world_trs_of(go):
        # compose TRS up the parent chain (no shear assumed) so runtime can rebuild
        # the GO from scratch without relying on bundled hierarchy
        try:
            def quat_mul(a, b):
                ax, ay, az, aw = a
                bx, by, bz, bw = b
                return [
                    aw * bx + ax * bw + ay * bz - az * by,
                    aw * by - ax * bz + ay * bw + az * bx,
                    aw * bz + ax * by - ay * bx + az * bw,
                    aw * bw - ax * bx - ay * by - az * bz,
                ]

            def rotate(q, v):
                x, y, z, w = q
                ux, uy, uz = x, y, z
                dot_uv = ux * v[0] + uy * v[1] + uz * v[2]
                dot_uu = ux * ux + uy * uy + uz * uz
                cx = uy * v[2] - uz * v[1]
                cy = uz * v[0] - ux * v[2]
                cz = ux * v[1] - uy * v[0]
                k = w * w - dot_uu
                return [
                    2 * dot_uv * ux + k * v[0] + 2 * w * cx,
                    2 * dot_uv * uy + k * v[1] + 2 * w * cy,
                    2 * dot_uv * uz + k * v[2] + 2 * w * cz,
                ]

            tr = None
            for comp in go.m_Component:
                c = comp.component if hasattr(comp, "component") else comp[1]
                if c.path_id in transforms:
                    tr = transforms[c.path_id]
                    break
            if tr is None:
                return None
            chain = []
            hops = 0
            while tr is not None and hops < 64:
                hops += 1
                chain.append(tr)
                father = tr.m_Father
                tr = transforms.get(father.path_id) if father.path_id else None
            pos, rot, scale = [0, 0, 0], [0, 0, 0, 1], [1, 1, 1]
            for t in reversed(chain):
                lp = [t.m_LocalPosition.x, t.m_LocalPosition.y, t.m_LocalPosition.z]
                lr = [t.m_LocalRotation.x, t.m_LocalRotation.y, t.m_LocalRotation.z, t.m_LocalRotation.w]
                ls = [t.m_LocalScale.x, t.m_LocalScale.y, t.m_LocalScale.z]
                scaled = [lp[i] * scale[i] for i in range(3)]
                off = rotate(rot, scaled)
                pos = [pos[i] + off[i] for i in range(3)]
                rot = quat_mul(rot, lr)
                scale = [scale[i] * ls[i] for i in range(3)]
            return {"pos": [round(float(v), 5) for v in pos],
                    "rot": [round(float(v), 6) for v in rot],
                    "scale": [round(float(v), 5) for v in scale]}
        except Exception:
            return None

    go_paths = {pid: path_of(go) for pid, go in gos.items()}
    for pid, go in gos.items():
        try:
            for comp in go.m_Component:
                c = comp.component if hasattr(comp, "component") else comp[1]
                comp_to_go[c.path_id] = pid
        except Exception:
            pass

    node_cache = {}
    results, owners = {}, {}
    for obj in sf.objects.values():
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            mb = obj.read(check_read=False)
            script = mb.m_Script.read()
        except Exception:
            continue
        cls = script.m_ClassName
        if cls not in TARGETS:
            continue
        ns = script.m_Namespace
        full = f"{ns}.{cls}" if ns else cls
        asm = script.m_AssemblyName
        go_pid = mb.m_GameObject.path_id
        go = gos.get(go_pid)
        row = {
            "path_id": obj.path_id,
            "go_id": go_pid,
            "go": go_paths.get(go_pid, "?"),
            "trs": trs_of(go) if go else None,
        }
        if cls == "IndoorTrigger" and go is not None:
            row["world_trs"] = world_trs_of(go)
        try:
            key = (asm, full)
            if key not in node_cache:
                node_cache[key] = patched_nodes(gen, asm, full, cls)
            data = obj.read_typetree(node_cache[key], check_read=cls in CORE)
            row["fields"] = sanitize(
                {k: v for k, v in data.items()
                 if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")})
        except Exception as e:
            row["parse_error"] = str(e)[:200]
        owners[obj.path_id] = go_pid
        results.setdefault(cls, []).append(row)

    # ref resolution table: component/GO path_id -> GO path (colliders resolve too)
    id_to_gopath = {}
    for cpid, gpid in comp_to_go.items():
        id_to_gopath[str(cpid)] = go_paths.get(gpid, "?")
    for gpid, p in go_paths.items():
        id_to_gopath[str(gpid)] = p

    return results, id_to_gopath


# the scene SpatialAudioSystem's external ScriptableObject refs (resolved by hand from
# its PPtrs + the level's externals table): the real per-location info asset and the
# occlusion tuning preset. both rebuilt at runtime via CreateInstance + reflection.
ASSET_REFS = [
    ("resources.assets", 45921, "location_info"),        # icebreaker_sound_info
    ("sharedassets397.assets", 454, "occlusion_settings"),  # GenericAudioOcclusionPreset
]


def extract_assets(gen):
    out = {}
    for fname, path_id, key in ASSET_REFS:
        try:
            env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(LEVELS_DIR / fname))
            sf = next(f for k, f in env.files.items() if str(k).endswith(fname))
            o = sf.objects[path_id]
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
            full = (scr.m_Namespace + "." if scr.m_Namespace else "") + scr.m_ClassName
            nodes = gen.get_nodes_up(scr.m_AssemblyName, full)
            d = o.read_typetree(nodes, check_read=False)
            out[key] = {
                "class": full,
                "name": getattr(mb, "m_Name", ""),
                "fields": sanitize({k: v for k, v in d.items()
                                    if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")}),
            }
            print(f"  asset {key}: {full} '{getattr(mb, 'm_Name', '')}'")
        except Exception as e:
            print(f"  asset {key} FAILED: {e}")
    return out


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    out = {
        "source": "retail 1.0 level698(Scripts)+level707(Sound), typetrees from SPT 0.16.9 Mono",
        "layout_notes": "room: retail areaBoxes array inserted after _bounds; portal: retail dropped portalType+state",
        "scenes": {},
        "assets": extract_assets(gen),
    }

    for scene_name, level in SCENES.items():
        print(f"--- {scene_name} ({level.name}) ---")
        env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(level))
        results, id_map = extract_scene(env, level.name, gen)
        scene_out = {}
        for cls in sorted(results):
            rows = results[cls]
            errs = sum(1 for r in rows if "parse_error" in r)
            print(f"  {cls}: {len(rows)}" + (f" ({errs} parse errors)" if errs else ""))
            scene_out[cls] = rows
        scene_out["_id_to_go"] = id_map
        out["scenes"][scene_name] = scene_out

    OUT.write_text(json.dumps(out, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
