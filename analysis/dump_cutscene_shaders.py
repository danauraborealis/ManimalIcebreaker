# what shaders does RETAIL run on the dynamic cutscene models (actors, helicopters)?
# the smap shader whites out on our dynamic spawns but works on static map geometry —
# if retail assigns a different (non-SMap) variant to skinned/dynamic stuff, that's
# the real convention and the fix is using the same shader retail does per material.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")

FILES = ["sharedassets709.assets"]  # cutscene content


def main():
    for fname in FILES:
        env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"),
                           str(LEVELS_DIR / fname))
        sf = next(f for k, f in env.files.items() if str(k).endswith(fname))
        print(f"=== {fname}: {sum(1 for o in sf.objects.values() if o.type.name == 'Material')} materials ===")
        shader_cache = {}
        rows = []
        for o in sf.objects.values():
            if o.type.name != "Material":
                continue
            try:
                m = o.read()
                sp = m.m_Shader
                key = (sp.m_FileID, sp.m_PathID)
                if key not in shader_cache:
                    name = "?"
                    try:
                        sh = sp.deref(sf).read()
                        name = getattr(sh, "m_ParsedForm", None)
                        name = name.m_Name if name is not None else getattr(sh, "m_Name", "?")
                    except Exception as e:
                        name = f"<unresolved {key}: {str(e)[:60]}>"
                    shader_cache[key] = name
                rows.append((m.m_Name, shader_cache[key]))
            except Exception:
                pass
        by_shader = {}
        for mat, sh in rows:
            by_shader.setdefault(sh, []).append(mat)
        for sh, mats in sorted(by_shader.items(), key=lambda kv: -len(kv[1])):
            print(f"\n[{len(mats):3d}] {sh}")
            for m in mats[:12]:
                print(f"      {m}")
            if len(mats) > 12:
                print(f"      ... +{len(mats)-12} more")


if __name__ == "__main__":
    main()
