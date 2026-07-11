# recovers BSG's AIPlaceInfo authoring from retail level706 (Icebreaker_AI) — the
# T1-T4 trigger volumes that make rogues attack when the player enters an area, plus
# the ambush places. mechanism (verified in the 0.16.9 decompile): AIPlaceInfo box
# (registered in AIPlaceInfoHolder.Places) + AIPlaceInfoLogicExUsecAttack{ConnectedZone}
# -> ExUsecBrainClass subscribes OnPlayerEnter for places whose ConnectedZone matches
# the bot's own zone -> player entry aggros that zone's rogues. same raw-bytes pipeline
# as spatial audio / weather.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\IcebreakerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "icebreaker_aiplaces.json"
LEVEL = "level706"  # Icebreaker_AI

TARGETS = {
    "AIPlaceInfo", "AIPlaceInfoTagillaAmbush", "AIPlaceWithPoint",
    "AIPlaceInfoLogicExUsecAttack", "AIPlaceInfoLogicAllEnemy", "AIPlaceLogicEventRaise",
    "AIPlaceInfoLogic", "ThrowGrenadePlace",
    "BotZone",  # needed so ConnectedZone refs resolve to zone names
}


# retail layout drift, hand-verified from hexdumps:
#   AIPlaceInfo: ONE extra int (value -1 observed) between CoversSpecial and
#     UseAsCoverGroupId. tail then ends byte-exact.
#   AIPlaceLogicEventRaise: ONE extra trailing int after ExitRaise.
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


def patched_nodes(gen, asm, full, cls):
    nodes = gen.get_nodes_up(asm, full)
    fl = flat(nodes)
    if cls in ("AIPlaceInfo", "AIPlaceInfoTagillaAmbush", "AIPlaceWithPoint"):
        out = []
        for row in fl:
            if row[0] == 1 and row[2] == "UseAsCoverGroupId":
                out.append([1, "int", "retailExtra_0", 0])
            out.append(row)
        fl = out
    elif cls.startswith("AIPlaceInfoLogic") or cls.startswith("AIPlaceLogic"):
        fl = fl + [[1, "int", "retailExtra_0", 0]]
    return to_tree(fl)


def sanitize(v):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            return {"ref": v["m_PathID"], "file": v["m_FileID"]} if v["m_FileID"] else {"ref": v["m_PathID"]}
        return {k: sanitize(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if hasattr(v, "__dict__"):
        return {k: sanitize(x) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(LEVELS_DIR / LEVEL))
    sf = next(f for k, f in env.files.items() if str(k).endswith(LEVEL))

    gos, transforms = {}, {}
    for o in sf.objects.values():
        try:
            if o.type.name == "GameObject":
                gos[o.path_id] = o.read()
            elif o.type.name in ("Transform", "RectTransform"):
                transforms[o.path_id] = o.read()
        except Exception:
            pass
    parent = {pid: tr.m_Father.path_id for pid, tr in transforms.items()}

    def go_tid(go):
        for comp in go.m_Component:
            c = comp.component if hasattr(comp, "component") else comp[1]
            if c.path_id in transforms:
                return c.path_id
        return 0

    def path_of(go):
        chain, t, hops = [], go_tid(go), 0
        while t in transforms and hops < 64:
            hops += 1
            g = gos.get(transforms[t].m_GameObject.path_id)
            chain.append(g.m_Name if g else "?")
            t = parent.get(t, 0)
        return "/".join(reversed(chain))

    def world_of(go):
        x = y = z = 0.0
        t, hops = go_tid(go), 0
        while t in transforms and hops < 64:
            hops += 1
            p = transforms[t].m_LocalPosition
            x += p.x; y += p.y; z += p.z
            t = parent.get(t, 0)
        return [round(x, 2), round(y, 2), round(z, 2)]

    node_cache, results, flagged = {}, {}, []
    zone_names = {}  # BotZone component path_id -> zone GO name (for ConnectedZone resolution)

    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        cls = scr.m_ClassName
        if cls not in TARGETS:
            continue
        go = gos.get(mb.m_GameObject.path_id)
        if cls == "BotZone":
            if go is not None:
                zone_names[str(o.path_id)] = go.m_Name
            continue  # only need the name mapping, not the fields
        full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
        row = {
            "path_id": o.path_id,
            "go": path_of(go) if go else "?",
            "world": world_of(go) if go else None,
            "class": full,
        }
        try:
            key = (scr.m_AssemblyName, full)
            if key not in node_cache:
                node_cache[key] = patched_nodes(gen, scr.m_AssemblyName, full, cls)
            try:
                data = o.read_typetree(node_cache[key], check_read=True)
            except Exception:
                data = o.read_typetree(node_cache[key], check_read=False)
                row["parse_flagged"] = True
                flagged.append(cls)
            row["fields"] = sanitize({k: v for k, v in data.items()
                                      if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")})
        except Exception as e:
            row["parse_error"] = str(e)[:200]
            flagged.append(f"{cls} FAILED")
        results.setdefault(cls, []).append(row)

    out = {
        "source": "retail 1.0 level706 (Icebreaker_AI) AIPlaceInfo layer",
        "zone_names": zone_names,
        "components": results,
    }
    OUT.write_text(json.dumps(out, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")
    for cls in sorted(results):
        n = len(results[cls])
        fl = sum(1 for r in results[cls] if r.get("parse_flagged") or r.get("parse_error"))
        print(f"  {cls}: {n}" + (f"  <-- {fl} FLAGGED" if fl else ""))
    print(f"  BotZone name mappings: {len(zone_names)}")


if __name__ == "__main__":
    main()
