# dumps every LootableContainer (Id + Template tpl) from the BUILT scene bundle into
# bundle_lootables.json — the input for gen_static_containers.py. run after EVERY
# bundle rebuild: the SDK regenerates container ids at rebake time, so only the bundle
# knows the ids the client will actually load.
import json
from pathlib import Path

import UnityPy

BUNDLE = Path(r"C:\Users\peard\Desktop\WTT-SDK-2022 Public\IcebreakerBundles\assets\content\locations\icebreaker\icebreaker_scenes.bundle")
OUT = Path(__file__).parent / "bundle_lootables.json"


def main():
    env = UnityPy.load(str(BUNDLE))
    rows = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            mb = obj.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        if scr.m_ClassName != "LootableContainer":
            continue
        tt = obj.read_typetree()
        rows.append({"Id": tt.get("Id", ""), "Template": tt.get("Template", "")})
    OUT.write_text(json.dumps({"containers": rows}, indent=1))
    print(f"wrote {OUT}: {len(rows)} containers")


if __name__ == "__main__":
    main()
