# retail LevelPhysicsSettings recovery. every vanilla map carries one, and its Awake is
# what calls Physics.RebuildBroadphaseRegions(bounds, subdivisions) to lay out the PhysX
# Multibox Pruning grid. our backport shipped without it, which is invisible on a normal
# load but fatal on a transit: the previous map's regions survive, every icebreaker
# collider lands outside them in the one overflow region, and that region caps at 64K.
#
# the retail Scripts scene has it on a GameObject called 'PhysicsSetup' (found via the
# inventory in extract_levelsettings.py), so the authentic bounds can be baked rather
# than guessed from a collider sweep.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_levelphysics.json"

LEVELS = [f"level{n}" for n in range(698, 710)]
TARGET = "LevelPhysicsSettings"


def flat(n, lvl=0, out=None):
    if out is None:
        out = []
    out.append([lvl, n.m_Type, n.m_Name, n.m_MetaFlag or 0])
    for c in (n.m_Children or []):
        flat(c, lvl + 1, out)
    return out


def to_tree(fl):
    from UnityPy.helpers.TypeTreeNode import TypeTreeNode
    return TypeTreeNode.from_list([TypeTreeNode(l, t, n, 0, 0, m_MetaFlag=m) for l, t, n, m in fl])


def sanitize(v):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            return None if v["m_PathID"] == 0 else {"ref": [v["m_FileID"], v["m_PathID"]]}
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


def main():
    print("loading Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    results = []
    for level in LEVELS:
        lp = LEVELS_DIR / level
        if not lp.exists():
            continue
        env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(lp))
        sf = next(f for k, f in env.files.items() if str(k).endswith(level))

        gos, transforms = {}, {}
        for o in sf.objects.values():
            try:
                if o.type.name == "GameObject":
                    gos[o.path_id] = o.read()
                elif o.type.name == "Transform":
                    transforms[o.path_id] = o.read()
            except Exception:
                pass

        node_cache = {}
        for o in sf.objects.values():
            if o.type.name != "MonoBehaviour":
                continue
            try:
                mb = o.read(check_read=False)
                scr = mb.m_Script.read()
            except Exception:
                continue
            if scr.m_ClassName != TARGET:
                continue

            go = gos.get(mb.m_GameObject.path_id)
            row = {"level": level, "go": go.m_Name if go else "?"}

            # the runtime does transform.TransformPoint(_mbpBounds.center), so the host's
            # own position has to travel with the values or the grid lands in the wrong place
            try:
                for t in (transforms.get(c.path_id) for c in (go.m_Component or [])):
                    if t is not None and hasattr(t, "m_LocalPosition"):
                        row["host_position"] = sanitize(t.m_LocalPosition)
                        break
            except Exception:
                pass

            try:
                full = (scr.m_Namespace + "." if scr.m_Namespace else "") + scr.m_ClassName
                key = (scr.m_AssemblyName, full)
                if key not in node_cache:
                    node_cache[key] = to_tree(flat(gen.get_nodes_up(scr.m_AssemblyName, full)))
                data = o.read_typetree(node_cache[key], check_read=False)
                row["fields"] = sanitize({k: v for k, v in data.items()
                                          if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")})
            except Exception as e:
                row["parse_error"] = str(e)[:300]
            results.append(row)

    OUT.write_text(json.dumps({"levelphysics": results}, indent=1))
    print(f"wrote {OUT}\n")
    for r in results:
        if "parse_error" in r:
            print(f"  PARSE ERROR {r['level']}/{r['go']}: {r['parse_error'][:160]}")
        else:
            print(f"  {r['level']}/{r['go']} host={r.get('host_position')}")
            print(f"    {json.dumps(r['fields'])}")


if __name__ == "__main__":
    main()
