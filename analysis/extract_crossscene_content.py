# retail cross-scene culling CONTENT recovery (levels 699-704): the SBG_ nodes in the
# content scenes carry the real renderer registries — PerfectCullingCrossSceneContent
# subclasses whose _bakeGroups arrays are what the packed cull data's ushort indices
# point into (groups rebuild bakeGroups at runtime by concatenating these, sorted by
# _contentGroupId). every object ref is exported as scene PATH + WORLD POS so Author 15
# can re-resolve it against the SDK's AssetRipper-rebuilt hierarchy (path first,
# name+position fallback — same convention as the doors tool).

import json
import math
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_crossscene_content.json"

LEVELS = {
    "level699": "Icebreaker_Outdoor",
    "level700": "Icebreaker_Indoor_01",
    "level701": "Icebreaker_Indoor_02",
    "level702": "Icebreaker_Indoor_03",
    "level703": "Icebreaker_Lights",
    "level704": "Icebreaker_Design_Stuff",
}
TARGET_CLASSES = {
    "PerfectCullingCrossSceneGroup",
    "PerfectCullingCrossSceneContentMeshes",
    "PerfectCullingCrossSceneContentDoors",
    "CrossSceneContentPortals",
    "PerfectCullingLightGroupPreProcess",
    "CrossSceneCullingGroupPreProcess",
}


def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def q_rot(q, v):
    qv = (v[0], v[1], v[2], 0.0)
    qc = (-q[0], -q[1], -q[2], q[3])
    r = q_mul(q_mul(q, qv), qc)
    return (r[0], r[1], r[2])


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))
    node_cache = {}
    out = {}

    for lvl, scene_name in LEVELS.items():
        env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(LEVELS_DIR / lvl))
        sf = next(f for k, f in env.files.items() if str(k).endswith(lvl))

        gos, transforms = {}, {}
        for o in sf.objects.values():
            try:
                if o.type.name == "GameObject":
                    gos[o.path_id] = o.read()
                elif o.type.name in ("Transform", "RectTransform"):
                    transforms[o.path_id] = o.read()
            except Exception:
                pass
        parent = {pid: tr.m_Father.path_id for pid, tr in transforms.items()}

        # component pid -> owning GO pid (from the GO side — no need to read components)
        comp2go = {}
        for gpid, g in gos.items():
            for comp in g.m_Component:
                c = comp.component if hasattr(comp, "component") else comp[1]
                comp2go[c.path_id] = gpid

        def go_tid(go):
            for comp in go.m_Component:
                c = comp.component if hasattr(comp, "component") else comp[1]
                if c.path_id in transforms:
                    return c.path_id
            return 0

        def sib_key(tid):
            g = gos.get(transforms[tid].m_GameObject.path_id)
            name = g.m_Name if g else "?"
            par = parent.get(tid, 0)
            if par not in transforms:
                return name
            k = 0
            for ch in (transforms[par].m_Children or []):
                if ch.path_id == tid:
                    break
                cg = gos.get(transforms[ch.path_id].m_GameObject.path_id) if ch.path_id in transforms else None
                if cg is not None and cg.m_Name == name:
                    k += 1
            return name if k == 0 else f"{name}~{k}"

        path_cache = {}

        def path_of_tid(tid):
            if tid in path_cache:
                return path_cache[tid]
            chain, t, hops = [], tid, 0
            while t in transforms and hops < 64:
                hops += 1
                chain.append(sib_key(t))
                t = parent.get(t, 0)
            p = "/".join(reversed(chain))
            path_cache[tid] = p
            return p

        world_cache = {}

        def world_of_tid(tid):
            if tid in world_cache:
                return world_cache[tid]
            tr = transforms[tid]
            lp = (tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z)
            lr = (tr.m_LocalRotation.x, tr.m_LocalRotation.y, tr.m_LocalRotation.z, tr.m_LocalRotation.w)
            ls = (tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z)
            par = parent.get(tid, 0)
            if par not in transforms:
                w = (lp, lr, ls)
            else:
                pp, pr, ps = world_of_tid(par)
                scaled = (lp[0] * ps[0], lp[1] * ps[1], lp[2] * ps[2])
                rot = q_rot(pr, scaled)
                w = (
                    (pp[0] + rot[0], pp[1] + rot[1], pp[2] + rot[2]),
                    q_mul(pr, lr),
                    (ps[0] * ls[0], ps[1] * ls[1], ps[2] * ls[2]),
                )
            world_cache[tid] = w
            return w

        stats = {"refs": 0, "resolved": 0, "external": 0, "dangling": 0}

        # ref table: refs serialize as {"r": index} into this level's "refs" list of
        # [path, x, y, z] — kills the 115MB-of-dicts problem and lets the editor
        # resolve each unique target exactly once.
        ref_table = []       # [path, x, y, z]
        ref_index = {}       # tid -> index

        def ref_to_path(v):
            fid, pid = v.get("m_FileID", 0), v.get("m_PathID", 0)
            if pid == 0:
                return None
            stats["refs"] += 1
            if fid != 0:
                stats["external"] += 1
                return {"ext": True}
            gpid = comp2go.get(pid, pid if pid in gos else None)
            if gpid is None or gpid not in gos:
                stats["dangling"] += 1
                return {"dangling": pid}
            tid = go_tid(gos[gpid])
            if tid == 0:
                stats["dangling"] += 1
                return {"dangling": pid}
            stats["resolved"] += 1
            if tid not in ref_index:
                wp = world_of_tid(tid)[0]
                ref_index[tid] = len(ref_table)
                ref_table.append([path_of_tid(tid), round(wp[0], 3), round(wp[1], 3), round(wp[2], 3)])
            return {"r": ref_index[tid]}

        def sanitize(v):
            if isinstance(v, dict):
                if "m_PathID" in v and "m_FileID" in v:
                    return ref_to_path(v)
                return {k: sanitize(x) for k, x in v.items()}
            if isinstance(v, (list, tuple)):
                return [sanitize(x) for x in v]
            if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
                return v
            if isinstance(v, float):
                return v if v == v and abs(v) != float("inf") else 0.0
            if hasattr(v, "__dict__"):
                return {k: sanitize(x) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
            return str(v)

        # lossless size cut: fields restored onto FRESH components, so zero/null/empty
        # values can be omitted — EXCEPT keys whose stub default isn't zero.
        KEEP_ALWAYS = {"_contentGroupId", "_contentHash"}

        def default_ish(v):
            if v is None or v is False:
                return True
            if isinstance(v, (int, float)) and v == 0:
                return True
            if isinstance(v, str) and v == "":
                return True
            if isinstance(v, list) and len(v) == 0:
                return True
            if isinstance(v, dict) and v and all(default_ish(x) for x in v.values()) and "r" not in v:
                return True
            return False

        def prune(v):
            if isinstance(v, dict):
                return {k: prune(x) for k, x in v.items() if k in KEEP_ALWAYS or not default_ish(x)}
            if isinstance(v, list):
                return [prune(x) for x in v]
            return v

        rows = []
        for o in sf.objects.values():
            if o.type.name != "MonoBehaviour":
                continue
            try:
                mb = o.read(check_read=False)
                scr = mb.m_Script.read()
            except Exception:
                continue
            if scr.m_ClassName not in TARGET_CLASSES:
                continue
            go = gos.get(mb.m_GameObject.path_id)
            tid = go_tid(go) if go else 0
            row = {
                "class": scr.m_ClassName,
                "go": path_of_tid(tid) if tid else "?",
            }
            try:
                full = (scr.m_Namespace + "." if scr.m_Namespace else "") + scr.m_ClassName
                key = (scr.m_AssemblyName, full)
                if key not in node_cache:
                    node_cache[key] = gen.get_nodes_up(scr.m_AssemblyName, full)
                data = o.read_typetree(node_cache[key], check_read=False)
                row["fields"] = prune(sanitize({k: v for k, v in data.items()
                                               if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")}))
            except Exception as e:
                row["parse_error"] = str(e)[:200]
            rows.append(row)

        out[lvl] = {"scene": scene_name, "components": rows, "refs": ref_table, "ref_stats": stats}
        ok = sum(1 for r in rows if "fields" in r)
        bad = sum(1 for r in rows if "parse_error" in r)
        print(f"{lvl} ({scene_name}): {len(rows)} components, {ok} parsed, {bad} DRIFTED; "
              f"refs={stats['refs']} resolved={stats['resolved']} ext={stats['external']} dangling={stats['dangling']}")
        for r in rows:
            f = r.get("fields") or {}
            bg = f.get("_bakeGroups") or f.get("bakeGroups")
            extra = f" bakeGroups={len(bg)}" if bg is not None else ""
            cgid = f.get("_contentGroupId")
            extra += f" contentGroupId={cgid}" if cgid is not None else ""
            print(f"   {r['class']} '{r['go']}'{extra} {'(DRIFTED: ' + r.get('parse_error', '') + ')' if 'parse_error' in r else ''}")

    OUT.write_text(json.dumps(out))
    print(f"\nwrote {OUT} ({OUT.stat().st_size / 1048576:.1f} MB)")


if __name__ == "__main__":
    main()
