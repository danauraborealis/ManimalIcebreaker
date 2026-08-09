# what did RETAIL statically batch? built player scenes strip m_StaticEditorFlags
# (why the AssetRipper export shows all-zero) but keep the RESULT on every renderer:
# m_StaticBatchInfo{firstSubMesh, subMeshCount} — subMeshCount > 0 means this renderer
# was combined into a static batch at build time. this is ground truth, no heuristics.
import json
import os

import UnityPy

LEVELS_DIR = r"C:\Users\peard\Desktop\IcebreakerLevels"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "retail_static_batch.json")

# scene index -> name from globalgamemanagers BuildSettings (levelN == scene index N)
scene_names = {}
try:
    env = UnityPy.load(os.path.join(LEVELS_DIR, "globalgamemanagers"))
    for obj in env.objects:
        if obj.type.name == "BuildSettings":
            tt = obj.read_typetree()
            for i, p in enumerate(tt.get("scenes", [])):
                scene_names[i] = os.path.splitext(os.path.basename(p))[0]
except Exception as e:
    print("buildsettings parse failed:", e)

result = {}
for n in range(698, 710):
    path = os.path.join(LEVELS_DIR, f"level{n}")
    if not os.path.exists(path):
        continue
    name = scene_names.get(n, "?")
    total = batched = 0
    batched_names = {}
    unbatched_names = {}
    try:
        env = UnityPy.load(path)
        objs = list(env.objects)
        go_by_id = {o.path_id: o for o in objs if o.type.name == "GameObject"}
        name_cache = {}

        def go_name(pid):
            if pid in name_cache:
                return name_cache[pid]
            nm = "?"
            o = go_by_id.get(pid)
            if o is not None:
                try:
                    nm = o.read_typetree().get("m_Name", "?")
                except Exception:
                    pass
            name_cache[pid] = nm
            return nm

        for obj in objs:
            if obj.type.name != "MeshRenderer":
                continue
            tt = obj.read_typetree()
            total += 1
            sub = tt.get("m_StaticBatchInfo", {}).get("subMeshCount", 0)
            nm = go_name(tt["m_GameObject"]["m_PathID"])
            if sub > 0:
                batched += 1
                batched_names[nm] = batched_names.get(nm, 0) + 1
            else:
                unbatched_names[nm] = unbatched_names.get(nm, 0) + 1
    except Exception as e:
        print(f"level{n}: FAILED {e}")
        continue

    pct = (100.0 * batched / total) if total else 0.0
    print(f"level{n:<4} {name:<30} renderers={total:<6} retail-batched={batched:<6} ({pct:0.1f}%)")
    result[f"level{n}"] = {
        "scene": name,
        "renderers": total,
        "batched": batched,
        "top_batched": sorted(batched_names.items(), key=lambda x: -x[1])[:12],
        "top_unbatched": sorted(unbatched_names.items(), key=lambda x: -x[1])[:12],
    }

json.dump(result, open(OUT, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print("\nwrote", OUT)
