# builds live_loot_markers.json (the Author 24 import manifest) from the SHIPPED
# looseLoot.json — i.e. it extracts exactly the live-1.0-derived entries that were
# merged directly into the json on 2026-08-02 (retail flare slots, live keycard
# spawns, story items, ~100 live-position pool points) so they can become scene
# markers and survive Author 12 re-exports. rerun only if the live merge changes.
#
# live-derived entries are recognized by template.Id: our generated points are
# iceloose_*/iceforced_*, live entries kept their dump ids ("rcp (1) [guid]",
# "item_ice_card_mech1 [guid]", ...). the crew-keycard and blowtorch position
# ADDITIONS live inside our own iceforced entries (group members named live_*/
# bsg_*) and are emitted as joinGroupOfTpl markers.

import json
import re
from pathlib import Path

LL = Path(r"C:\Users\peard\Desktop\ManimalIcebreaker\icebreaker-server\db\looseLoot.json")
OUT = Path(__file__).parent / "live_loot_markers.json"

FLARE = "6217726288ed9f0845317459"
ENGINE = "69bb3e54d6c67f6265004ab9"
C1 = "69bb3f278af9f360ee010a7a"
CREW = "69bb3ec9f609db77390b0e1a"
TORCH_VANILLA = "67ab3d4b83869afd170fdd3f"
TORCH_CLONE = "9a449693dff5334122ed7388"


def root_tpl(t):
    return next(i["_tpl"] for i in t["Items"] if i["_id"] == t["Root"])


def name_of(t):
    # "item_ice_card_mech1 [e4ce46b5-...]" -> "live_item_ice_card_mech1_e4ce46b5"
    m = re.match(r"(.+?)\s*\[([0-9a-f]{8})", t["Id"])
    base = (m.group(1) if m else t["Id"]).strip().replace(" ", "_").replace("(", "").replace(")", "")
    guid = m.group(2) if m else "noguid"
    return f"live_{base}_{guid}"


def poses_of(t):
    # markers are one-per-candidate-position; GroupPositions include the head pos,
    # so emit members only — reordered head-first so the generator's head/Position
    # stays live's. singles fall back to the template pose.
    p, r = t["Position"], t["Rotation"]
    if len(t.get("GroupPositions") or []) > 1:
        members = [(g["Position"], g.get("Rotation") or {"x": 0, "y": 0, "z": 0})
                   for g in t["GroupPositions"]]
        members.sort(key=lambda me: abs(me[0]["x"] - p["x"]) + abs(me[0]["y"] - p["y"]) + abs(me[0]["z"] - p["z"]))
        return members
    return [(p, r)]


# the live entry NAMES are BSG's own pool-category labels ("Meds (36)", "Electrionic
# (19)" — typos are BSG's) — in retail these are CATEGORY pool points; the dump only
# shows the single item each rolled that raid. mapped names become OUR category pool
# spots (they roll properly); unmapped names stay fixed-item overrides — deliberate
# for led100 (retail's dedicated LEDX spot), mods_rf (CNC adapter) and the item_ice_*
# story pieces. defib->Med and tetriz->Tech are user calls (08-03).
POOL_BY_PREFIX = [
    ("meds", "Med"),           # covers Meds + Meds_supl_sup (defibrillator)
    ("food_drink", "Food"),
    ("drink", "Food"),
    ("electrionic", "Tech"),
    ("electronic", "Tech"),
    ("jeverly", "Valuables"),  # "Jeverly_sup" = jewelry supply
    ("jewelry", "Valuables"),
    ("tools", "Tools"),
    ("constr", "Tools"),       # sealing foam
    ("tetriz_kay", "Tech"),
]


def pool_for(entry_id):
    low = entry_id.lower()
    for pre, pool in POOL_BY_PREFIX:
        if low.startswith(pre):
            return pool
    return None


def main():
    data = json.loads(LL.read_text(encoding="utf-8"))
    markers = []

    def emit(name, pos, rot, tpl, prob, group="", pool_point=False, join="", pool=""):
        markers.append({
            "name": name,
            "pos": [round(pos["x"], 6), round(pos["y"], 6), round(pos["z"], 6)],
            "euler": [round(rot["x"], 5), round(rot["y"], 5), round(rot["z"], 5)],
            "overrideTpl": tpl,
            "prob": round(prob, 3),
            "group": group,
            "poolPoint": pool_point,
            "joinGroupOfTpl": join,
            "pool": pool,
        })

    # --- live forced entries (flares, engine keycard, C-1 keycard)
    n_forced = 0
    for f in data["spawnpointsForced"]:
        t = f["template"]
        if t["Id"].startswith("iceforced_"):
            # our authored entries — but live's crew/torch position ADDITIONS were
            # merged into these as group members named live_*/bsg_*
            join_tpl = {CREW: CREW, TORCH_CLONE: f"{TORCH_VANILLA},{TORCH_CLONE}"}.get(root_tpl(t))
            if not join_tpl:
                continue
            for g in t.get("GroupPositions") or []:
                if not (g.get("Name", "").startswith("live_") or g.get("Name", "").startswith("bsg_")):
                    continue
                # markers keep the VANILLA torch tpl (gen_loose_loot TPL_REPLACE swaps it)
                mk_tpl = TORCH_VANILLA if root_tpl(t) == TORCH_CLONE else root_tpl(t)
                emit(f"{name_of(t)}_{g['Name']}", g["Position"],
                     g.get("Rotation") or {"x": 0, "y": 0, "z": 0},
                     mk_tpl, f["probability"], join=join_tpl)
            continue
        tpl = root_tpl(t)
        poses = poses_of(t)
        group = name_of(t) if len(poses) > 1 else ""
        for k, (p, r) in enumerate(poses):
            emit(f"{name_of(t)}_g{k}" if group else name_of(t), p, r, tpl, f["probability"], group=group)
        n_forced += 1

    # --- live pool points: category spots where the name maps, fixed-item otherwise
    n_pool = 0
    n_cat = 0
    for sp in data["spawnpoints"]:
        t = sp["template"]
        if t["Id"].startswith("iceloose_"):
            continue
        cat = pool_for(t["Id"])
        tpl = "" if cat else root_tpl(t)
        poses = poses_of(t)
        group = name_of(t) if len(poses) > 1 else ""
        for k, (p, r) in enumerate(poses):
            emit(f"{name_of(t)}_g{k}" if group else name_of(t), p, r, tpl,
                 sp["probability"], group=group, pool_point=bool(tpl), pool=cat or "")
        n_pool += 1
        if cat:
            n_cat += 1

    out = {
        # old flare spot + the two keycards whose spawns live REPLACED outright
        "deleteByTpl": [FLARE, ENGINE, C1],
        "markers": markers,
    }
    OUT.write_text(json.dumps(out, indent=1), encoding="utf-8")
    joins = sum(1 for m in markers if m["joinGroupOfTpl"])
    print(f"wrote {OUT}: {len(markers)} markers ({n_forced} live forced entries, "
          f"{n_pool} live pool points of which {n_cat} category-mapped, {joins} group-joins), "
          f"deleteByTpl={len(out['deleteByTpl'])}")


if __name__ == "__main__":
    main()
