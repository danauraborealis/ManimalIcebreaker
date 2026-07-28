# recover the 4 cutscene driver components from level709 (CutsceneObjects,
# CutscenePlayableDirector, CutsceneCameraReference, SignalReceiver). pass 1
# couldn't parse them: the classes are 1.0-only, absent from SPT's 0.16.9
# Assembly-CSharp. workaround: mirror SPT Managed into a temp folder with the
# WIP 1.0 Assembly-CSharp swapped in, and feed THAT to TypeTreeGenerator.
# the CutsceneObjects activate/deactivate lists tell the runtime driver which
# scene objects retail toggles at cutscene start/end — refs resolved to
# hierarchy paths so the driver can find them by name.

import json
import shutil
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
WIP = Path(r"G:\downloads\Assembly-CSharp.dll")
TMP = Path(r"C:\Users\peard\AppData\Local\Temp\claude\managed_wip10")
OUT = Path(__file__).parent / "icebreaker_cutscene_drivers.json"

TARGETS = {"CutsceneObjects", "CutscenePlayableDirector", "CutsceneCameraReference",
           "SignalReceiver", "CutsceneProcess", "StartCutsceneByStartRaid"}


def build_tmp_managed():
    if TMP.exists() and (TMP / "Assembly-CSharp.dll").stat().st_size == WIP.stat().st_size:
        return
    TMP.mkdir(parents=True, exist_ok=True)
    for f in MANAGED.glob("*.dll"):
        dest = TMP / f.name
        if not dest.exists():
            shutil.copyfile(f, dest)
    shutil.copyfile(WIP, TMP / "Assembly-CSharp.dll")


def sanitize(v, path_of):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v and len(v) <= 3:
            pid = v["m_PathID"]
            if pid == 0:
                return None
            if v["m_FileID"] != 0:
                return {"externalRef": [v["m_FileID"], pid]}
            return {"ref": path_of(pid)}
        return {k: sanitize(x, path_of) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x, path_of) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if hasattr(v, "__dict__"):
        return {k: sanitize(x, path_of) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def main():
    print("staging Managed+WIP folder...")
    build_tmp_managed()
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(TMP))

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"),
                       str(LEVELS_DIR / "level709"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("level709"))

    gos, transforms = {}, {}
    for o in sf.objects.values():
        try:
            if o.type.name == "GameObject":
                gos[o.path_id] = o.read()
            elif o.type.name in ("Transform", "RectTransform"):
                transforms[o.path_id] = o.read()
        except Exception:
            pass

    # hierarchy path resolution (name chain via transform parents)
    tf_by_go = {}
    for pid, t in transforms.items():
        try:
            tf_by_go[t.m_GameObject.path_id] = pid
        except Exception:
            pass

    def go_path(go_pid):
        try:
            tpid = tf_by_go.get(go_pid)
            parts = []
            while tpid:
                t = transforms[tpid]
                g = gos.get(t.m_GameObject.path_id)
                parts.append(g.m_Name if g else "?")
                f = t.m_Father
                tpid = f.path_id if f and f.path_id else None
            return "/".join(reversed(parts))
        except Exception:
            return f"<go {go_pid}>"

    def path_of(pid):
        # pid may be a GameObject, Transform, or other component
        if pid in gos:
            return go_path(pid)
        if pid in transforms:
            return go_path(transforms[pid].m_GameObject.path_id)
        o = sf.objects.get(pid)
        if o is not None and o.type.name == "MonoBehaviour":
            try:
                mb = o.read(check_read=False)
                return f"{go_path(mb.m_GameObject.path_id)} <{o.type.name}>"
            except Exception:
                pass
        try:
            comp = sf.objects[pid].read(check_read=False)
            gpid = comp.m_GameObject.path_id
            return f"{go_path(gpid)} <{sf.objects[pid].type.name}>"
        except Exception:
            return f"<pathID {pid}>"

    results = []
    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        if scr.m_ClassName not in TARGETS:
            continue
        full = (scr.m_Namespace + "." if scr.m_Namespace else "") + scr.m_ClassName
        row = {"pathID": o.path_id, "class": full,
               "go": go_path(mb.m_GameObject.path_id)}
        try:
            tree = gen.get_nodes_up(scr.m_AssemblyName, full)
            data = o.read_typetree(tree, check_read=False)
            row["fields"] = sanitize({k: v for k, v in data.items()
                                      if k not in ("m_GameObject", "m_Script")}, path_of)
        except Exception as e:
            row["parse_error"] = str(e)[:300]
        results.append(row)

    OUT.write_text(json.dumps(results, indent=1))
    print(f"wrote {OUT}")
    for r in results:
        status = "OK" if "fields" in r else "FAIL: " + r.get("parse_error", "?")
        print(f"  {r['class']} @ {r['go']}: {status}")


if __name__ == "__main__":
    main()
