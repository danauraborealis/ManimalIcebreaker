# inventory the retail Icebreaker cutscene timeline (level709). the AssetRipper
# export produced EMPTY husks for every Timeline object (root .playable + all
# tracks lost their fields), so this reads the real graph straight from the level
# file. Timeline types are stock Unity package code (Unity.Timeline.dll) and both
# builds are 2022.3.43f2 -> typetrees should match with zero drift, unlike the
# BSG classes that needed surgery.
#
# pass 1 (this script): dump every timeline-related object (PlayableDirector,
# TimelineAsset, tracks, clip playable assets, markers) with full fields + refs
# resolved to names/paths, so we can see the graph shape, the track types in play
# (stock vs BSG custom), and what external assets (anims/audio) it pulls.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_cutscene_timeline.json"

LEVEL = "level709"


def sanitize(v):
    if isinstance(v, dict):
        return {k: sanitize(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if isinstance(v, bytes):
        return f"<bytes {len(v)}>"
    if hasattr(v, "__dict__"):
        return {k: sanitize(x) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"),
                       str(LEVELS_DIR / LEVEL))
    sf = next(f for k, f in env.files.items() if str(k).endswith(LEVEL))

    # externals list: fileID N in a PPtr means sf.externals[N-1]
    externals = [e.path for e in sf.externals]
    print("externals:", externals)

    gos = {}
    scripts = {}   # path_id -> (class, assembly) for MonoScript refs
    monos = []     # (path_id, obj)
    directors = []
    anims = {}     # path_id -> AnimationClip name (in-level)
    for o in sf.objects.values():
        t = o.type.name
        if t == "GameObject":
            try:
                gos[o.path_id] = o.read().m_Name
            except Exception:
                gos[o.path_id] = "<unreadable>"
        elif t == "MonoBehaviour":
            monos.append(o)
        elif t == "PlayableDirector":
            directors.append(o)
        elif t == "AnimationClip":
            try:
                anims[o.path_id] = o.read().m_Name
            except Exception:
                anims[o.path_id] = "<unreadable>"

    # resolve each MonoBehaviour's script to find timeline-ish ones. the MonoScript
    # lives in globalgamemanagers.assets usually — read the m_Script PPtr via UnityPy's
    # cross-file resolution.
    timeline_objs = {}
    script_cache = {}

    def script_of(o):
        try:
            raw = o.read(check_read=False)
            sp = raw.m_Script
            key = (sp.m_FileID, sp.m_PathID)
            if key in script_cache:
                return script_cache[key]
            ms = sp.deref(sf).read()
            val = (ms.m_ClassName, ms.m_AssemblyName)
            script_cache[key] = val
            return val
        except Exception as e:
            return (None, str(e))

    interesting_assemblies = ("Unity.Timeline", "UnityEngine.Director",
                              "UnityEngine.Timeline")
    for o in monos:
        cls, asm = script_of(o)
        if cls is None:
            continue
        if (asm and any(a in asm for a in interesting_assemblies)) or \
           (cls and ("Track" in cls or "Playable" in cls or "Timeline" in cls or
                     "Cutscene" in cls or "Marker" in cls)):
            try:
                tree = gen.get_nodes(asm, cls) if asm else None
            except Exception:
                tree = None
            data = None
            if tree is not None:
                try:
                    data = sanitize(o.read_typetree(tree))
                except Exception as e:
                    data = {"__parse_error": str(e)}
            timeline_objs[o.path_id] = {
                "class": cls, "assembly": asm, "data": data,
            }

    out = {
        "externals": externals,
        "directors": [],
        "timeline_objects": timeline_objs,
        "in_level_animclips": anims,
        "gameobject_names": {str(k): v for k, v in gos.items()},
    }
    for d in directors:
        try:
            out["directors"].append(sanitize(d.read_typetree()))
        except Exception as e:
            out["directors"].append({"__parse_error": str(e)})

    OUT.write_text(json.dumps(out, indent=1))
    classes = {}
    for v in timeline_objs.values():
        classes[v["class"]] = classes.get(v["class"], 0) + 1
    print(f"wrote {OUT}")
    print(f"{len(timeline_objs)} timeline-ish MonoBehaviours:")
    for c, n in sorted(classes.items(), key=lambda kv: -kv[1]):
        print(f"  {n:3d}  {c}")
    print(f"{len(out['directors'])} PlayableDirector(s), {len(anims)} in-level AnimationClips")


if __name__ == "__main__":
    main()
