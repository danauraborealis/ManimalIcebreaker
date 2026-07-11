# builds the icebreaker server mod's staticLoot.json (per-container-type loot pools):
# labs' pools verbatim + our per-container additions baked in. our own file instead of
# borrowing labs in-memory means no shared-reference leaks into real labs raids and
# hand-tuning is a text edit. rerun after changing EXTRA + restart the server.

import json
from pathlib import Path

LABS_STATIC = Path(r"D:\SPTDev\SPT\SPT_Data\database\locations\laboratory\staticLoot.json")
OUTS = [
    Path(r"C:\Users\peard\Desktop\ManimalIcebreaker\icebreaker-server\db\staticLoot.json"),
    Path(r"D:\SPTDev\SPT\user\mods\ManimalIcebreaker\db\staticLoot.json"),
]

# container tpl -> additions. weights are relative to that pool's own scale
# (duffle pool runs 1..6700, median ~1100).
EXTRA = {
    # duffle bag: the backported 1.0 icebreaker items (WTT-ContentBackport
    # must be installed or the server cant resolve the tpls)
    # endgame map — the backport rares run hotter than labs-equivalent weights
    "578f87a3245977356274f2cb": [
        ("69bb4203f94327bc0f0230cd", 400),  # IBX Gigachad cryptographic processor (rare)
        ("69bb424e99f3fda8f107247d", 900),  # Memento Server RAM Module
        ("69bb43df99f3fda8f1072483", 1078), # Nooby Shield potassium iodide tablets (ophthalmoscope-tier)
    ],
    # Medbag SMU06 (the ship's med containers — NOT 5909d50c, thats the Toolbox;
    # tpl names verified against locales after the med/tool swap bug): the
    # backported meds ride with the meds. medbag sum ~152k.
    "5909d24f86f77466f56e6855": [
        ("69bb43df99f3fda8f1072483", 370),  # Nooby Shield potassium iodide tablets (ophthalmoscope-tier ~0.24%)
        ("6937ecf8628ee476240c07cb", 230),  # Tigzresq splint (rarer than alu splint ~0.15%)
    ],
}


def main():
    sl = json.loads(LABS_STATIC.read_text())
    for tpl, extras in EXTRA.items():
        if tpl not in sl:
            print(f"WARNING: labs staticLoot has no pool '{tpl}' — additions skipped")
            continue
        for extra_tpl, weight in extras:
            sl[tpl]["itemDistribution"].append({"tpl": extra_tpl, "relativeProbability": weight})
        total = sum(e["relativeProbability"] for e in sl[tpl]["itemDistribution"])
        for extra_tpl, weight in extras:
            print(f"pool {tpl}: {extra_tpl} at {100.0 * weight / total:.2f}% per rolled item slot")
        print(f"pool {tpl}: +{len(extras)} items ({len(sl[tpl]['itemDistribution'])} total)")
    text = json.dumps(sl, indent=1)
    for o in OUTS:
        o.parent.mkdir(parents=True, exist_ok=True)
        o.write_text(text)
        print(f"wrote {o} ({len(sl)} container pools)")


if __name__ == "__main__":
    main()
