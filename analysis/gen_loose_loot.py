# builds the icebreaker server mod's looseLoot.json from the Author 12 marker export
# (icebreaker_loose_spots.json) + labs' per-container staticLoot pools. each authored
# spot becomes one spawnpoint: labs pool items as candidates at labs weights, spawned
# at the marker's exact pose. spawnpointCount.mean = expected fills per raid
# (sum of authored probabilities).

import hashlib
import json
from pathlib import Path

SPOTS = Path(__file__).parent / "icebreaker_loose_spots.json"
DB_LOCATIONS = Path(r"D:\SPTDev\SPT\SPT_Data\database\locations")
ITEMS_DB = Path(r"D:\SPTDev\SPT\SPT_Data\database\templates\items.json")
HANDBOOK = Path(r"D:\SPTDev\SPT\SPT_Data\database\templates\handbook.json")
STIM_PARENT = "5448f3a64bdc2d60728b456a"  # Stimulator class — every injector derives from it

# ENDGAME PRICE TILT (user call 07-17): this is an endgame map, so within these
# pools the pricier handbook items should roll more often than their labs-borrowed
# weights say. weight *= (price / pool median)^ALPHA, clamped — a 10x-median item
# lands ~2.5x its old weight, a dirt-cheap one drops to ~0.6x. deliberately mild:
# relative weights, so an uncapped boost would make one LEDX-tier item eat the pool.
PRICE_TILT_POOLS = {"Tech", "Valuables", "Tools", "Food"}
TILT_ALPHA = 0.4
TILT_MIN, TILT_MAX = 0.6, 3.0

# live 1.0 calibration (user call 08-02): live raids averaged ~258 loose items;
# sum-of-authored-probs would give ~280 after the live-position imports, so the
# mean is pinned to lives measurement instead. None = old sum-of-probs behavior.
MEAN_OVERRIDE = 258.0

# tpl remaps applied to authored overrideTpls AND pool entries — survives Author 12
# re-exports (the Unity markers keep the vanilla tpl; the swap happens here).
TPL_REPLACE = {
    # vanilla BBQ-S43 labyrinth torch -> our USABLE blowtorch clone (quickslot
    # draw + hold-fire burner; registered by the icebreaker server mod)
    "67ab3d4b83869afd170fdd3f": "9a449693dff5334122ed7388",
}
OUTS = [
    Path(r"C:\Users\peard\Desktop\ManimalIcebreaker\icebreaker-server\db\looseLoot.json"),
    Path(r"D:\SPTDev\SPT\user\mods\ManimalIcebreaker\db\looseLoot.json"),
]

# pool name -> (source map, container tpl) whose itemDistribution we borrow.
# labs for everything it stocks; the ration crate isn't a labs container so
# Food borrows lighthouse's. TPLS VERIFIED AGAINST LOCALE NAMES — the obvious
# guesses were wrong two ways (5909d50c is the TOOLBOX, 5909d24f the MEDBAG;
# 578f8778 is the JACKET, the real safe is 578f8782) and it put hardware in
# the med pool for a raid.
POOLS = {
    "Tech":      ("laboratory", "59139c2186f77411564f8e42"),  # PC block
    "Med":       ("laboratory", "5909d24f86f77466f56e6855"),  # Medbag SMU06 (meds + stims)
    "Tools":     ("laboratory", "5909d50c86f774659e6aaebe"),  # Toolbox
    "Weapons":   ("laboratory", "5909d7cf86f77470ee57d75a"),  # Weapon box
    "Misc":      ("laboratory", "578f87a3245977356274f2cb"),  # Duffle bag
    "Valuables": ("laboratory", "578f8782245977354405a1e3"),  # Safe
    "Food":      ("lighthouse", "5d6fd13186f77424ad2a8c69"),  # Ration supply crate (35 items incl aquamari)
}

# per-pool tpl blocklist applied to the borrowed distribution BEFORE extras
EXCLUDE_ITEMS = {
    # no smokes in the food pool
    "Food": {
        "573476f124597737e04bf328",  # Wilston cigarettes
        "573476d324597737da2adc13",  # Malboro Cigarettes
        "5734770f24597738025ee254",  # Strike Cigarettes
        "573475fb24597737fb1379e1",  # Apollo Soyuz cigarettes
    },
    # no raw cash at valuables spots — jewelry/intel/figurines only
    "Valuables": {
        "5449016a4bdc2d6f028b456f",  # Roubles
        "5696686a4bdc2da3298b456a",  # Dollars
        "569668774bdc2da2298b4568",  # Euros
    },
}

# per-pool additions on top of the borrowed distribution. weights are relative to
# the source pool (labs PC pool commons run ~90-135k, rares ~18k; lighthouse food
# runs ~500-6000) — tune in place.
EXTRA_ITEMS = {
    # the backported 1.0 icebreaker electronics (WTT-ContentBackport must be
    # installed or the server cant resolve these tpls)
    # endgame map — rares run hotter here than their labs-equivalent weights
    "Tech": [
        ("69bb4203f94327bc0f0230cd", 16000),  # IBX Gigachad cryptographic processor (rare)
        ("69bb424e99f3fda8f107247d", 20000),  # Memento Server RAM Module
        ("69bb41c03b5fb75517065960", 12000),  # Ultralink satellite communication module
        ("5d0378d486f77420421a5ff4", 10000),  # Military power filter (rare)
        ("5d1b2ffd86f77425243e8d17", 10000),  # NIXXOR lens
        ("590a391c86f774385a33c404", 40000),  # Magnet (common junk)
        ("6389c70ca33d8c4cdf4932c6", 8000),   # Electronic components
        # SSD removed — the labs PC pool already stocks it (was double-entered)
    ],
    # the backported hydraulic fluid rides with the tools (toolbox pool: sum
    # ~432k, commons 5-23k; the LOOTABLE variant — 699f0976... is the QuestItem
    # twin, dont swap them)
    "Tools": [
        ("699f09de81a6c812900a77b7", 4300),  # AMG-10 hydraulic fluid (~1%)
        # user adds 2026-07-18: of the requested wrench/firesteel/cable-cutter/gun-lube
        # set, only the lube was missing from the labs toolbox base pool
        ("5bc9b355d4351e6d1509862a", 2000),  # #FireKlean gun lube (~0.5%, firesteel-tier)
    ],
    # medical hardware belongs with the meds (medbag pool: 42 items, sum ~152k,
    # commons 2-15k — weights rescaled to keep the same per-slot percentages)
    "Med": [
        ("69bb435ff609db77390b0e25", 2500),   # Aceso Xpress semi-automatic biochemical analyzer (~1.6%)
        ("5c0530ee86f774697952d952", 2200),   # LEDX Skin Transilluminator (rare, ~1.4%)
        ("69bb43df99f3fda8f1072483", 370),    # Nooby Shield potassium iodide tablets (ophthalmoscope-tier ~0.24%)
        ("6937ecf8628ee476240c07cb", 230),    # Tigzresq splint (rarer than alu splint ~0.15%)
    ],
    # every figurine/statue in the base game + backport, riding the safe pool
    # (sum ~126k, median 357): regulars at 200, iconic/premium ones at 80
    "Valuables": [
        ("573478bc24597738002c6175", 200),  # Horse figurine
        ("59e3639286f7741777737013", 150),   # Bronze lion figurine
        ("59e3658a86f7741776641ac4", 200),  # Cat figurine
        ("5bc9bc53d4351e00367fbcee", 150),   # Golden rooster figurine
        ("5e54f62086f774219b0f1937", 200),  # Raven figurine
        ("62a091170b9d3c46de5b6cf2", 200),  # Axel parrot figurine
        ("655c652d60d0ac437100fed7", 200),  # BEAR operative figurine
        ("655c663a6689c676ce57af85", 200),  # USEC operative figurine
        ("655c669103999d3c810c025b", 200),  # Cultist figurine
        ("655c66e40b2de553b618d4b8", 150),   # Politician Mutkevich figurine
        ("655c673673a43e23e857aebd", 200),  # Scav figurine
        ("655c67782a1356436041c9c5", 200),  # Ryzhy figurine
        ("655c67ab0d37ca5135388f4b", 150),   # Ded Moroz figurine
        ("66572b8d80b1cd4b6a67847f", 200),  # Den figurine
        ("66572be36a723f7f005a066e", 150),   # Reshala figurine
        ("66572c82ad599021091c6118", 150),   # Killa figurine
        ("66572cbdad599021091c611a", 150),   # Tagilla figurine
        ("679b9cce4e4ed4b3b40ae5c5", 200),  # Nailhead figurine
        ("679b9d43597ba2ed120c3d44", 200),  # Petya Crooker figurine
        ("679b9d4b3374fb14f40efe6d", 200),  # Count Bloodsucker figurine
        ("679b9d55708cfcb2060b9ae3", 200),  # Xenoalien figurine
        ("679b9d6390622daf9708da76", 200),  # Pointy guy figurine
        ("6841b6b674a3c16f5e03d653", 200),  # PMC Origins figurine
        # backport set (WTT-ContentBackport required)
        ("68f25c64b2b53abd200b954f", 150),  # Prapor figurine
        ("68f25ce9b2b53abd200b9551", 150),  # Therapist figurine
        ("68f25da3b84c6e1f8a09cffb", 150),  # Fence figurine
        ("68f25eba19a2b503d70dc28f", 150),  # Skier figurine
        ("68f25eef19a2b503d70dc291", 150),  # Peacekeeper figurine
        ("68f25f1201fb9de974058be4", 150),  # Mechanic figurine
        ("68f25f3a5d977110860c3350", 150),  # Ragman figurine
        ("68f25faa22c8979ee308f4da", 150),  # Jaeger figurine
        ("68f25fdf928cd23ddf0471fb", 150),  # Lightkeeper figurine
        ("68f26063b2b53abd200b9553", 150),  # Ref figurine
        ("68f260d7b84c6e1f8a09cffd", 150),  # Hideout Cat figurine
        ("68f2608301fb9de974058be6", 150),  # BTR figurine
        ("68f26124b84c6e1f8a09cfff", 150),  # Walking Tank figurine
        ("68f26161b84c6e1f8a09d001", 150),  # Escape figurine
        ("68f261f6928cd23ddf0471fd", 150),  # Mastichin figurine
        ("68f11224197b61ffe703b16f", 150),  # Hardened PMC figurine
        ("690c70a2a1461a01d605a1bd", 150),  # Elvisvista figurine
        # backport update5 collectibles (WTT-ContentBackport required)
        ("69f9d319c906cd16da03b374", 150),  # SheefGG piggy bank (₽71k)
        ("69f9d547b98cc4120608692a", 150),  # DesmondPilak CD (₽53k)
    ],
    # the ration crate pool has no booze or the canister — add them
    "Food": [
        ("5d40407c86f774318526545a", 700),    # Tarkovskaya vodka
        ("5d403f9186f7743cac3f229b", 500),    # Dan Jackiel whiskey
        ("5d1b376e86f774252519444e", 300),    # Fierce Hatchling moonshine (rare)
        ("62a09f32621468534a797acb", 900),    # Pevko Light beer
        ("5d1b33a686f7742523398398", 450),    # Canister with purified water ("superwater", rare)
        ("69774bb0a247161ff1068335", 1500),   # Can of duck pate (backported 1.0 delicacy)
    ],
}


# LIVE 1.0 INJECTIONS (user call 08-02): items live's dumps spawn on the icebreaker
# that our borrowed pools lacked, injected per family into every matching pool.
# weights are FINAL (already live-count-calibrated against the tilted pool medians),
# so these append AFTER apply_price_tilt — do not fold them into EXTRA_ITEMS.
LIVE_INJECT = {
    "Food": [
        ("544fb6cc4bdc2d34748b456e", 156),   # Slickers chocolate bar
        ("575062b524597720a31c09a1", 156),   # Can of Ice Green tea
        ("57513f9324597720a7128161", 156),   # Pack of Grand juice
        ("57513fcc24597720a31c09a6", 156),   # Pack of Vita juice
        ("5751435d24597720a27126d1", 470),   # Can of Max Energy energy drink
        ("57514643245977207f2c2d09", 313),   # Can of TarCola soda
        ("590c5f0d86f77413997acfab", 1568),  # MRE ration pack
        ("59e3556c86f7741776641ac2", 156),   # Ox bleach
        ("5e8f3423fd7471236e6e3b64", 1568),  # Norvinsky Yadreniy premium kvass
        ("60b0f93284c20f0feb453da7", 1568),  # Can of RatCola soda
        ("656df4fec921ad01000481a2", 313),   # Pack of instant noodles
        ("65815f0e647e3d7246384e14", 1568),  # Pack of Tarker dried meat
    ],
    "Med": [
        ("59e3556c86f7741776641ac2", 228),   # Ox bleach (household chems family)
    ],
    # the phone/electronics junk family lands in all three tech-ish pools, scaled
    # to each pool's weight range
    "Tech": [
        ("56742c324bdc2d150f8b456d", 9680),   # Broken GPhone smartphone
        ("5909e99886f7740c983b9984", 4840),   # USB Adapter
        ("5bc9b720d4351e450201234b", 29041),  # Golden 1GPhone smartphone
        ("5c12620d86f7743f8b198b72", 29041),  # Tetriz portable game console
        ("5c1265fc86f7743f896a21c2", 33882),  # Broken GPhone X smartphone
        ("5d0377ce86f774186372f689", 19361),  # Iridium military thermal vision module
        ("5d03784a86f774203e7e0c4d", 24201),  # Military gyrotachometer
        ("5d1b2fa286f77425227d1674", 14520),  # Electric motor
        ("5d1b309586f77425227d1676", 9680),   # Broken LCD
    ],
    "Intel": [
        ("56742c324bdc2d150f8b456d", 80),     # Broken GPhone smartphone
        ("5909e99886f7740c983b9984", 40),     # USB Adapter
        ("5bc9b720d4351e450201234b", 240),    # Golden 1GPhone smartphone
        ("5c12620d86f7743f8b198b72", 240),    # Tetriz portable game console
        ("5c1265fc86f7743f896a21c2", 280),    # Broken GPhone X smartphone
        ("5d0377ce86f774186372f689", 160),    # Iridium military thermal vision module
        ("5d03784a86f774203e7e0c4d", 200),    # Military gyrotachometer
        ("5d1b2fa286f77425227d1674", 120),    # Electric motor
        ("5d1b309586f77425227d1676", 80),     # Broken LCD
    ],
    "Tools": [
        ("56742c284bdc2d98058b456d", 507),    # Crickent lighter
        ("56742c324bdc2d150f8b456d", 1014),   # Broken GPhone smartphone
        ("57347b8b24597737dd42e192", 507),    # Classic matches
        ("5909e99886f7740c983b9984", 507),    # USB Adapter
        ("590a373286f774287540368b", 1014),   # Dry fuel
        ("59fafb5d86f774067a6f2084", 1014),   # Propane tank (5L)
        ("5b43575a86f77424f443fe62", 2029),   # Fuel conditioner
        ("5bc9b720d4351e450201234b", 3044),   # Golden 1GPhone smartphone
        ("5c12620d86f7743f8b198b72", 3044),   # Tetriz portable game console
        ("5c1265fc86f7743f896a21c2", 3551),   # Broken GPhone X smartphone
        ("5d0377ce86f774186372f689", 2029),   # Iridium military thermal vision module
        ("5d03784a86f774203e7e0c4d", 2537),   # Military gyrotachometer
        ("5d1b2fa286f77425227d1674", 1522),   # Electric motor
        ("5d1b309586f77425227d1676", 1014),   # Broken LCD
        ("5e2af37686f774755a234b65", 1522),   # SurvL Survivor Lighter
        ("63a0b208f444d32d6f03ea1e", 1014),   # Fierce Blow sledgehammer
    ],
}


# Intel pool: no container base — authored outright. paperwork/media commons up
# top, the money items (folders + textbooks) at really-rare weights.
INTEL_POOL = [
    ("590c645c86f77412b01304d9", 1200),  # Diary
    ("590c651286f7741e566b6461", 1200),  # Slim diary
    ("62a0a124de7ac81993580542", 900),   # Topographic survey maps
    ("62a09e73af34e73a266d932a", 900),   # BakeEzy cook book
    ("590c639286f774151567fa95", 700),   # Tech manual
    ("62a09e974f842e1bd12da3f0", 700),   # Video cassette with the Cyborg Killer movie
    ("61bf7c024770ee6f9c6b8b53", 600),   # Secure magnetic tape cassette
    ("590c37d286f77443be3d7827", 450),   # SAS drive
    ("590c392f86f77444754deb29", 350),   # SSD drive
    ("5c05300686f7746dce784e5d", 350),   # VPX Flash Storage Module
    ("62a0a16d0b9d3c46de5b6e97", 350),   # Military flash drive
    ("6389c8fb46b54c634724d847", 90),    # Silicon Optoelectronic Integrated Circuits textbook (really rare)
    ("6389c92d52123d5dd17f8876", 90),    # Advanced Electronic Materials textbook (really rare)
    ("5c12613b86f7743bbe2c3f76", 80),    # Intelligence folder (really rare)
    ("6389c8c5dbfd5e4b95197e6b", 50),    # TerraGroup "Blue Folders" materials (really rare)
    # backport (WTT-ContentBackport update5)
    ("69f9d60b5de6674f08060f2a", 90),    # Dunduk floppy disk (₽67k collectible — textbook tier)
]


def mongo_from(seed):
    return hashlib.md5(seed.encode()).hexdigest()[:24]


def apply_price_tilt(pool_dists):
    prices = {}
    for e in json.loads(HANDBOOK.read_text(encoding="utf8"))["Items"]:
        prices[e["Id"]] = e.get("Price") or 0
    # backported items aren't in the static handbook — WTT-ContentBackport injects
    # them at server runtime. pull handbookPriceRoubles straight from its configs.
    backport = Path(r"D:\SPTDev\SPT\user\mods\WTT-ContentBackport\db\CustomItems")
    found = 0
    for cfg in backport.rglob("*.json"):
        try:
            d = json.loads(cfg.read_text(encoding="utf8"))
        except Exception:
            continue
        if not isinstance(d, dict):
            continue
        for tpl, entry in d.items():
            if isinstance(entry, dict) and isinstance(entry.get("handbookPriceRoubles"), (int, float)):
                prices[tpl] = entry["handbookPriceRoubles"]
                found += 1
    print(f"price tilt: {found} backport item prices merged into handbook")
    names = None
    for name in sorted(PRICE_TILT_POOLS):
        dist = pool_dists.get(name)
        if not dist:
            continue
        priced = [(e, prices.get(e["tpl"], 0)) for e in dist]
        known = sorted(p for _, p in priced if p > 0)
        if not known:
            print(f"{name} pool: no handbook prices found — tilt skipped")
            continue
        median = known[len(known) // 2]
        movers = []
        for e, p in priced:
            if p <= 0:
                continue  # not in handbook (odd backport tpl) — leave the hand weight
            factor = max(TILT_MIN, min(TILT_MAX, (p / median) ** TILT_ALPHA))
            old = e["relativeProbability"]
            e["relativeProbability"] = max(1, int(round(old * factor)))
            movers.append((factor, p, e["tpl"], old, e["relativeProbability"]))
        movers.sort(reverse=True)
        print(f"{name} pool tilt (median {median} rub): "
              + ", ".join(f"{t[2][:8]} p={t[1]} x{t[0]:.2f} {t[3]}->{t[4]}" for t in movers[:5])
              + f" ... {sum(1 for t in movers if t[0] > 1)} boosted / {sum(1 for t in movers if t[0] < 1)} reduced")


def main():
    spots = json.loads(SPOTS.read_text())["spots"]

    static_cache = {}
    pool_dists = {}
    for name, (srcmap, tpl) in POOLS.items():
        if srcmap not in static_cache:
            static_cache[srcmap] = json.loads((DB_LOCATIONS / srcmap / "staticLoot.json").read_text())
        src = static_cache[srcmap]
        if tpl not in src:
            print(f"WARNING: {srcmap} staticLoot has no pool for {name} ({tpl}) — spots with this pool are skipped")
            continue
        excl = EXCLUDE_ITEMS.get(name, set())
        dist = [e for e in src[tpl]["itemDistribution"] if e["tpl"] not in excl]
        if excl:
            print(f"{name} pool: {len(src[tpl]['itemDistribution']) - len(dist)} item(s) excluded")
        for extra_tpl, weight in EXTRA_ITEMS.get(name, []):
            dist.append({"tpl": extra_tpl, "relativeProbability": weight})
        pool_dists[name] = dist

    for dist in pool_dists.values():
        for e in dist:
            e["tpl"] = TPL_REPLACE.get(e["tpl"], e["tpl"])

    apply_price_tilt(pool_dists)

    # Stims pool: built from the item db, not a container — every injector at
    # uniform weight
    items_db = json.loads(ITEMS_DB.read_text(encoding="utf8"))
    stims = [k for k, v in items_db.items()
             if v.get("_parent") == STIM_PARENT and v.get("_type") == "Item"]
    pool_dists["Stims"] = [{"tpl": t, "relativeProbability": 100} for t in sorted(stims)]
    print(f"Stims pool: {len(stims)} injectors")

    pool_dists["Intel"] = [{"tpl": t, "relativeProbability": w} for t, w in INTEL_POOL]

    # live-injected items go in post-tilt with their final calibrated weights
    for name, extras in LIVE_INJECT.items():
        dist = pool_dists.get(name)
        if dist is None:
            print(f"WARNING: LIVE_INJECT pool '{name}' does not exist — skipped")
            continue
        present = {e["tpl"] for e in dist}
        for tpl, weight in extras:
            if tpl in present:
                print(f"WARNING: LIVE_INJECT {tpl} already in {name} pool — skipped")
                continue
            dist.append({"tpl": tpl, "relativeProbability": weight})
        print(f"{name} pool: +{len(extras)} live-injected item(s)")

    # group collapse: spots sharing a non-empty group name become ONE spawnpoint
    # whose GroupPositions lists every member pose — the game picks one per raid
    # (retail's randomized-keycard mechanic). pool/override/prob come from the
    # first member; mismatched members get a warning.
    merged = []
    by_group = {}
    for s in spots:
        g = (s.get("group") or "").strip()
        if not g:
            merged.append(s)
            continue
        if g in by_group:
            head = by_group[g]
            h_ovr = (head.get("overrideTpl") or "").strip()
            s_ovr = (s.get("overrideTpl") or "").strip()
            # pool only matters when there's no override; override spots ignore it
            if h_ovr != s_ovr:
                print(f"WARNING: group '{g}' members disagree on overrideTpl — using '{h_ovr or 'pool roll'}'")
            elif not h_ovr and head.get("pool") != s.get("pool"):
                print(f"WARNING: group '{g}' members disagree on pool — using '{head.get('pool')}'")
            head["groupPoses"].append((s["pos"], s.get("euler", [0, 0, 0])))
        else:
            s = dict(s)
            s["groupPoses"] = [(s["pos"], s.get("euler", [0, 0, 0]))]
            by_group[g] = s
            merged.append(s)
    for g, head in by_group.items():
        print(f"group '{g}': {len(head['groupPoses'])} candidate position(s)")
    spots = merged

    spawnpoints = []
    forced = []
    total_prob = 0.0
    skipped = 0
    for i, s in enumerate(spots):
        x, y, z = s["pos"]
        rx, ry, rz = s.get("euler", [0, 0, 0])
        prob = max(0.01, min(1.0, float(s.get("prob", 0.35))))
        group_poses = s.get("groupPoses")
        is_group = bool(group_poses) and len(group_poses) > 1
        group_positions = [
            {
                "Name": f"groupPoint[{k}]",
                "Weight": 1,
                "Position": {"x": p[0], "y": p[1], "z": p[2]},
                "Rotation": {"x": e[0], "y": e[1], "z": e[2]},
            }
            for k, (p, e) in enumerate(group_poses)
        ] if is_group else []

        # specific-item spot: exactly this tpl, emitted into spawnpointsForced —
        # forced entries roll independently of the spawnpointCount budget, so
        # probability 1 there means guaranteed every raid
        override = (s.get("overrideTpl") or "").strip()
        override = TPL_REPLACE.get(override, override)
        if override and s.get("poolPoint"):
            # live-1.0 import spots: a specific item that rolls against the loot
            # budget like any pool point (forced would spawn it every raid)
            iid = mongo_from(f"icelive_{i}_{override}")
            key = str(hash((i, override)) & 0x7FFFFFFF)
            total_prob += prob
            spawnpoints.append({
                "locationId": f"({x}, {y}, {z})",
                "probability": prob,
                "template": {
                    "Id": f"icelive_{i:03d}",
                    "IsContainer": False,
                    "useGravity": False,
                    "randomRotation": False,
                    "Position": {"x": x, "y": y, "z": z},
                    "Rotation": {"x": rx, "y": ry, "z": rz},
                    "IsGroupPosition": is_group,
                    "GroupPositions": group_positions,
                    "IsAlwaysSpawn": False,
                    "Root": iid,
                    "Items": [
                        {"composedKey": key, "_id": iid, "_tpl": override, "upd": {"StackObjectsCount": 1}}
                    ],
                },
                "itemDistribution": [
                    {"composedKey": {"key": key}, "relativeProbability": 1}
                ],
            })
            continue
        if override:
            iid = mongo_from(f"iceforced_{i}_{override}")
            forced.append({
                "locationId": f"({x}, {y}, {z})",
                "probability": prob,
                "template": {
                    "Id": f"iceforced_{i:03d}",
                    "IsContainer": False,
                    "useGravity": False,
                    "randomRotation": False,
                    "Position": {"x": x, "y": y, "z": z},
                    "Rotation": {"x": rx, "y": ry, "z": rz},
                    "IsGroupPosition": is_group,
                    "GroupPositions": group_positions,
                    "IsAlwaysSpawn": False,
                    "Root": iid,
                    "Items": [
                        {"_id": iid, "_tpl": override, "upd": {"StackObjectsCount": 1}}
                    ],
                },
            })
            continue

        dist = pool_dists.get(s["pool"])
        if dist is None:
            skipped += 1
            continue
        total_prob += prob
        items, item_dist = [], []
        for j, entry in enumerate(dist):
            key = str(hash((i, entry["tpl"])) & 0x7FFFFFFF)
            iid = mongo_from(f"iceloose_{i}_{j}_{entry['tpl']}")
            items.append({
                "composedKey": key,
                "_id": iid,
                "_tpl": entry["tpl"],
                "upd": {"StackObjectsCount": 1},
            })
            item_dist.append({
                "composedKey": {"key": key},
                "relativeProbability": entry["relativeProbability"],
            })
        spawnpoints.append({
            "locationId": f"({x}, {y}, {z})",
            "probability": prob,
            "template": {
                "Id": f"iceloose_{s['pool']}_{i:03d}",
                "IsContainer": False,
                "useGravity": False,
                "randomRotation": False,
                "Position": {"x": x, "y": y, "z": z},
                "Rotation": {"x": rx, "y": ry, "z": rz},
                "IsGroupPosition": is_group,
                "GroupPositions": group_positions,
                "IsAlwaysSpawn": False,
                "Root": items[0]["_id"],
                "Items": items,
            },
            "itemDistribution": item_dist,
        })

    mean = MEAN_OVERRIDE if MEAN_OVERRIDE is not None else round(total_prob, 2)
    # std is live-calibrated, NOT mean*0.25: the three live raids counted 254/261/260
    # loose items — a spread of SEVEN. the old derived std (64.5) made one raid in
    # four roll under ~214 ("loot feels scarce today"), which retail never does.
    out = {
        "spawnpointCount": {"mean": mean, "std": 10.0},
        "spawnpointsForced": forced,
        "spawnpoints": spawnpoints,
    }
    text = json.dumps(out, indent=1)
    for o in OUTS:
        o.parent.mkdir(parents=True, exist_ok=True)
        o.write_text(text)
        print(f"wrote {o} ({len(spawnpoints)} pool spawnpoints + {len(forced)} specific-item, "
              f"~{total_prob:.1f} pool items/raid{f', {skipped} skipped' if skipped else ''})")


if __name__ == "__main__":
    main()
