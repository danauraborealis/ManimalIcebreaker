# generates the suburbs (icebreaker) staticContainers.json for the server mod from
# the BUILT BUNDLE's container dump (bundle_lootables.json, produced by the UnityPy
# scan) — NOT the retail extraction: the SDK regenerates container Ids at rebake
# time, so only the bundle knows the ids the client will actually have. same wrapper
# shape as SPT's per-map files ({staticWeapons, staticContainers, staticForced}).
# every container is an independent probability-1 entry — retail's
# LootableContainersGroup pick-one-position mechanic is NOT reproduced yet.
# REGENERATE THIS whenever the scene bundle is rebuilt (ids can shift again).

import hashlib
import json
from pathlib import Path

SRC = Path(__file__).parent / "bundle_lootables.json"
OUTS = [
    Path(r"C:\Users\peard\Desktop\ManimalIcebreaker\icebreaker-server\db\staticContainers.json"),
    Path(r"D:\SPTDev\SPT\user\mods\ManimalIcebreaker\db\staticContainers.json"),
]

def mongo_from(seed):
    # deterministic 24-hex id from the container id — stable across regenerations,
    # unique within the file (container ids are unique)
    return hashlib.md5(seed.encode()).hexdigest()[:24]

def main():
    d = json.loads(SRC.read_text())
    lcs = d["containers"]
    entries = []
    for f in sorted(lcs, key=lambda r: r["Id"]):
        root = mongo_from(f["Id"])
        entries.append({
            "probability": 1,
            "template": {
                "Id": f["Id"],
                "IsContainer": True,
                "useGravity": False,
                "randomRotation": False,
                "Position": {"x": 0, "y": 0, "z": 0},
                "Rotation": {"x": 0, "y": 0, "z": 0},
                "IsGroupPosition": False,
                "GroupPositions": [],
                "IsAlwaysSpawn": False,  # retail scene truth; probability is the ONLY spawn knob
                "Root": root,
                "Items": [
                    {"_id": root, "_tpl": f["Template"], "upd": {"StackObjectsCount": 1}}
                ],
            },
        })
    out = {"staticWeapons": [], "staticContainers": entries, "staticForced": []}
    text = json.dumps(out, indent=1)
    for o in OUTS:
        o.parent.mkdir(parents=True, exist_ok=True)
        o.write_text(text)
        print(f"wrote {o} ({len(entries)} containers)")

if __name__ == "__main__":
    main()
