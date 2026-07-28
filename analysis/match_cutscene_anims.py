# match the 39 retail cutscene AnimationClips (resources.assets pathIDs, from
# icebreaker_cutscene_graph.json) to AssetRipper's exported .anim files. named
# clips match by m_Name; the 17 "Recorded (N)" clips are ambiguous (every cutscene
# in the game has its own "Recorded (N)") so AssetRipper suffixed collisions with
# _0/_1/... -> disambiguate by m_AnimationClipSettings.m_StopTime (duration)
# fingerprint, fall back to curve-block count. output feeds the SDK import step.

import json
import re
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
EXPORT = LEVELS_DIR / "AssetRipper_export_20260704_204314" / "ExportedProject" / "Assets" / "AnimationClip"
GRAPH = Path(__file__).parent / "icebreaker_cutscene_graph.json"
OUT = Path(__file__).parent / "icebreaker_cutscene_anim_map.json"


def yaml_stop_time(p):
    # cheap regex scan — full YAML parse of a 50MB anim is pointless
    txt = p.read_text(encoding="utf-8", errors="ignore")
    m = re.search(r"m_StopTime: ([0-9.eE+-]+)", txt)
    stop = float(m.group(1)) if m else None
    curves = txt.count("path: ")
    name_m = re.search(r"m_Name: (.+)", txt)
    return stop, curves, (name_m.group(1).strip() if name_m else None)


def main():
    graph = json.loads(GRAPH.read_text())
    clip_pids = [(int(pid), v["name"]) for pid, v in graph["objects"].items()
                 if v.get("unityType") == "AnimationClip"]
    print(f"{len(clip_pids)} retail clips to match")

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"),
                       str(LEVELS_DIR / "resources.assets"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("resources.assets"))

    retail = {}
    for pid, name in clip_pids:
        o = sf.objects[pid]
        try:
            c = o.read()
            # built clips: duration lives in m_MuscleClip.m_StopTime
            stop = None
            mc = getattr(c, "m_MuscleClip", None)
            if mc is not None:
                stop = getattr(mc, "m_StopTime", None)
            bindings = 0
            cbc = getattr(c, "m_ClipBindingConstant", None)
            if cbc is not None:
                bindings = len(getattr(cbc, "genericBindings", []) or [])
            retail[pid] = {"name": name, "stop": stop, "bindings": bindings}
        except Exception as e:
            retail[pid] = {"name": name, "error": str(e)[:200]}

    # index exported anims by base name
    exported = {}
    for p in EXPORT.glob("*.anim"):
        base = re.sub(r"_\d+$", "", p.stem)
        exported.setdefault(base, []).append(p)

    mapping = {}
    problems = []
    for pid, info in retail.items():
        name = info["name"]
        cands = exported.get(name, [])
        if not cands:
            problems.append(f"{pid} '{name}': NO exported candidates")
            continue
        if len(cands) == 1:
            mapping[pid] = {"file": cands[0].name, "name": name, "how": "unique-name"}
            continue
        stop = info.get("stop")
        best = None
        scored = []
        for p in cands:
            ystop, ycurves, yname = yaml_stop_time(p)
            d = abs((ystop or -999) - (stop or 999))
            scored.append((d, ycurves, p))
        scored.sort(key=lambda t: t[0])
        best = scored[0]
        if best[0] < 0.01 and (len(scored) < 2 or scored[1][0] > 0.01):
            mapping[pid] = {"file": best[2].name, "name": name,
                            "how": f"stoptime {stop:.3f}"}
        else:
            ties = [s for s in scored if s[0] < 0.01]
            if len(ties) > 1:
                # duration tie — fall back to curve count vs retail binding count
                ties.sort(key=lambda t: abs(t[1] - info.get("bindings", 0)))
                mapping[pid] = {"file": ties[0][2].name, "name": name,
                                "how": f"stoptime tie, curve-count {ties[0][1]} vs bindings {info.get('bindings')}"}
                if abs(ties[0][1] - info.get("bindings", 0)) > 2:
                    problems.append(f"{pid} '{name}': weak curve-count match {ties[0][2].name}")
            else:
                problems.append(f"{pid} '{name}': no stop-time match "
                                f"(retail {stop}, best {scored[0][0]:.3f} off, {scored[0][2].name})")

    OUT.write_text(json.dumps({"mapping": {str(k): v for k, v in mapping.items()},
                               "problems": problems}, indent=1))
    print(f"wrote {OUT}: {len(mapping)}/{len(retail)} matched")
    for m in list(mapping.items())[:8]:
        print(" ", m)
    if problems:
        print("PROBLEMS:")
        for p in problems:
            print("  " + p)


if __name__ == "__main__":
    main()
