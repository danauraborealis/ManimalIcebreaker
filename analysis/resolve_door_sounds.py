# resolve every retail door's OpenSound/ShutSound/SqueakSound externalRefs
# (fileID, pathID) to actual AudioClip NAMES — the doors currently all ship the
# metal foley set, but retail assigns wood sets to the interior cabin doors.
# output feeds Author 1R so each door gets its retail clip set by name.

import json
from pathlib import Path

import UnityPy

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
DOORS = Path(__file__).parent / "icebreaker_doors.json"
OUT = Path(__file__).parent / "icebreaker_door_sounds.json"

_env_cache = {}
_name_cache = {}


def load_file(fname):
    if fname not in _env_cache:
        p = LEVELS_DIR / fname
        if not p.exists():
            _env_cache[fname] = None
        else:
            env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(p))
            _env_cache[fname] = next((f for k, f in env.files.items()
                                      if str(k).endswith(fname)), None)
    return _env_cache[fname]


def externals_of(level):
    sf = load_file(level)
    return [e.path.replace("Library/", "").split("/")[-1] for e in sf.externals] if sf else []


def clip_name(fname, pid):
    key = (fname, pid)
    if key in _name_cache:
        return _name_cache[key]
    name = None
    sf = load_file(fname)
    if sf is not None:
        o = sf.objects.get(pid)
        if o is not None and o.type.name == "AudioClip":
            try:
                name = o.read().m_Name
            except Exception:
                name = f"<unreadable {pid}>"
        elif o is not None:
            name = f"<{o.type.name} {pid}>"
    _name_cache[key] = name
    return name


def main():
    doors = json.loads(DOORS.read_text())["components"]
    ext_cache = {}
    out_rows = []
    sets = {}
    for cls, rows in doors.items():
        if cls not in ("Door", "KeycardDoor", "SlidingDoor"):
            continue
        for row in rows:
            level = row.get("level")
            f = row.get("fields", {})
            if level not in ext_cache:
                ext_cache[level] = externals_of(level)
            exts = ext_cache[level]
            entry = {"class": cls, "level": level, "go": row.get("go")}
            sig = []
            for k in ("OpenSound", "ShutSound", "SqueakSound"):
                names = []
                for ref in (f.get(k) or []):
                    er = ref.get("externalRef") if isinstance(ref, dict) else None
                    if not er:
                        continue
                    fid, pid = er
                    fname = exts[fid - 1] if 0 < fid <= len(exts) else None
                    names.append(clip_name(fname, pid) if fname else f"<file {fid}?>")
                entry[k] = names
                sig.append(f"{k}={','.join(str(n) for n in names)}")
            out_rows.append(entry)
            sets.setdefault(" | ".join(sig), []).append(entry["go"])

    OUT.write_text(json.dumps({"doors": out_rows}, indent=1))
    print(f"wrote {OUT}: {len(out_rows)} doors, {len(sets)} distinct clip sets\n")
    for sig, gos in sorted(sets.items(), key=lambda kv: -len(kv[1])):
        print(f"[{len(gos)} doors] {sig[:170]}")
        print(f"    e.g. {gos[0][:110]}")


if __name__ == "__main__":
    main()
