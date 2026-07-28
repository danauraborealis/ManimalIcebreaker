# resolve the cutscene director's 58 binding TARGETS (level709 pathIDs) to
# hierarchy paths + component types, so Author 17 can wire bindings by transform
# path instead of GlobalObjectId fileIDs (which came back all-None in editor).
# output merges into cutscene_build_data.json as bindings[i].scenePath/kind.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
BUILD = Path(r"C:\Users\peard\Desktop\WTT-SDK-2022 Public\Assets\Icebreaker_Import\CutsceneAnims\cutscene_build_data.json")


def main():
    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"),
                       str(LEVELS_DIR / "level709"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("level709"))

    gos, transforms, comps = {}, {}, {}
    for o in sf.objects.values():
        t = o.type.name
        try:
            if t == "GameObject":
                gos[o.path_id] = o.read()
            elif t in ("Transform", "RectTransform"):
                transforms[o.path_id] = o.read()
            else:
                comps[o.path_id] = (t, o)
        except Exception:
            pass

    tf_by_go = {}
    for pid, t in transforms.items():
        try:
            tf_by_go[t.m_GameObject.path_id] = pid
        except Exception:
            pass

    # sibling-ordinal path (name~k) — same scheme as the door/audio rebakes so
    # duplicate names resolve deterministically
    children = {}
    for pid, t in transforms.items():
        f = t.m_Father
        children.setdefault(f.path_id if f else 0, []).append(pid)

    def ordinal(tpid):
        t = transforms[tpid]
        f = t.m_Father
        sibs = children.get(f.path_id if f else 0, [])
        name = gos[t.m_GameObject.path_id].m_Name if t.m_GameObject.path_id in gos else "?"
        same = [s for s in sibs if s in transforms and
                transforms[s].m_GameObject.path_id in gos and
                gos[transforms[s].m_GameObject.path_id].m_Name == name]
        if len(same) <= 1:
            return name
        return f"{name}~{same.index(tpid)}"

    def go_path(go_pid):
        tpid = tf_by_go.get(go_pid)
        parts = []
        while tpid:
            parts.append(ordinal(tpid))
            f = transforms[tpid].m_Father
            tpid = f.path_id if f and f.path_id else None
        return "/".join(reversed(parts))

    def resolve(pid):
        if pid in gos:
            return go_path(pid), "GameObject"
        if pid in transforms:
            return go_path(transforms[pid].m_GameObject.path_id), "Transform"
        if pid in comps:
            tname, o = comps[pid]
            try:
                c = o.read(check_read=False)
                return go_path(c.m_GameObject.path_id), tname
            except Exception:
                return None, tname
        return None, "?"

    build = json.loads(BUILD.read_text())
    misses = 0
    for b in build["bindings"]:
        path, kind = resolve(b["sceneFileId"])
        b["scenePath"] = path
        b["kind"] = kind
        if path is None:
            misses += 1
            print(f"  UNRESOLVED pathID {b['sceneFileId']} ({kind})")
    BUILD.write_text(json.dumps(build, indent=1))
    kinds = {}
    for b in build["bindings"]:
        kinds[b["kind"]] = kinds.get(b["kind"], 0) + 1
    print(f"updated {BUILD}: {len(build['bindings'])} bindings, {misses} unresolved")
    print("target kinds:", kinds)
    for b in build["bindings"][:10]:
        print(f"  {b['trackPid']} -> [{b['kind']}] {b['scenePath']}")


if __name__ == "__main__":
    main()
