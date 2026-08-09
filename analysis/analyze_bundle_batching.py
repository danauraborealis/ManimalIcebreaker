# is OUR BUILT scene bundle statically batched? same ground truth as the retail
# check: MeshRenderer.m_StaticBatchInfo.subMeshCount > 0 only ever set by a build
# with batching (or a runtime Combine, which can't be in a file on disk).
import os

import UnityPy

BUNDLE = r"D:\SPTDevSonic\BepInEx\plugins\ManimalIcebreaker\streamingassets\Windows\assets\content\locations\icebreaker\icebreaker_scenes.bundle"

env = UnityPy.load(BUNDLE)
per_file = {}
for obj in env.objects:
    if obj.type.name != "MeshRenderer":
        continue
    src = os.path.basename(getattr(obj.assets_file, "name", "?") or "?")
    tot, bat = per_file.get(src, (0, 0))
    sub = 0
    try:
        sub = obj.read_typetree().get("m_StaticBatchInfo", {}).get("subMeshCount", 0)
    except Exception:
        pass
    per_file[src] = (tot + 1, bat + (1 if sub > 0 else 0))

gt, gb = 0, 0
for src, (tot, bat) in sorted(per_file.items()):
    gt += tot
    gb += bat
    print(f"{src:<50} renderers={tot:<7} batched={bat:<7} ({100.0*bat/tot if tot else 0:0.1f}%)")
print(f"\nTOTAL: renderers={gt} batched={gb} ({100.0*gb/gt if gt else 0:0.1f}%)")
