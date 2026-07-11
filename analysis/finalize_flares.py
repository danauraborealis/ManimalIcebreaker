# package the flare extraction for the client: components (from extract_flares.py) +
# atlas containers + material specs, deployed to plugins/AIDataDumper/flares/ with the
# two PNGs. run AFTER extract_flares.py.

import json
import shutil
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

HERE = Path(__file__).parent
D = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
PLUGIN_DIR = Path(r"D:\SPTDev\BepInEx\plugins\AIDataDumper\flares")


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


def main():
    comp = json.loads((HERE / "icebreaker_flares.json").read_text())

    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
    env = UnityPy.load(str(D / "globalgamemanagers.assets"), str(D / "sharedassets3.assets"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("sharedassets3.assets"))

    node = to_tree(flat(gen.get_nodes_up("Assembly-CSharp.dll", "MultiFlare.ProFlareAtlas")))
    adata = sf.objects.get(20).read_typetree(node, check_read=False)
    containers = [{"name": c.get("_name"), "rect": c.get("_rect")} for c in adata.get("_containers", [])]

    mats = {}
    for pid, slot in ((5, "normal"), (6, "shit"), (4, "overlap")):
        m = sf.objects.get(pid).read()
        sh = m.m_Shader.read()
        shader = getattr(getattr(sh, "m_ParsedForm", None), "m_Name", None) or sh.m_Name
        # the tuned property sets matter: _DepthOffset=0.1 is the on-surface depth
        # tolerance — zero-default materials lose the depth test to their own lamp
        sp = m.m_SavedProperties
        floats = {str(k): float(v) for k, v in (sp.m_Floats.items() if isinstance(sp.m_Floats, dict) else sp.m_Floats)}
        colors = {}
        for k, v in (sp.m_Colors.items() if isinstance(sp.m_Colors, dict) else sp.m_Colors):
            colors[str(k)] = [v.r, v.g, v.b, v.a] if hasattr(v, "r") else [v["r"], v["g"], v["b"], v["a"]]
        mats[slot] = {"name": m.m_Name, "shader": shader, "floats": floats, "colors": colors}

    fl_rows = [r for r in comp["components"]["FlareLight"] if "fields" in r]
    settings = next((r for r in comp["components"]["FlareSceneSettings"] if "fields" in r), None)

    out = {
        "flares": fl_rows,
        "settings": settings["fields"] if settings else None,
        "atlas": {"name": "FlareAtlas", "containers": containers},
        "materials": mats,
    }
    PLUGIN_DIR.mkdir(parents=True, exist_ok=True)
    (PLUGIN_DIR / "flares.json").write_text(json.dumps(out))
    shutil.copy(HERE / "icebreaker_flare_atlas.png", PLUGIN_DIR / "flare_atlas.png")
    shutil.copy(HERE / "icebreaker_flare_shit.png", PLUGIN_DIR / "flare_shit.png")
    print(f"packaged: {len(fl_rows)} flares, {len(containers)} atlas containers, "
          f"materials={[m['shader'] for m in mats.values()]} -> {PLUGIN_DIR}")


if __name__ == "__main__":
    main()
