#!/usr/bin/env python3
# generates the icebreaker location base.json for the SPT server mod by cloning
# factory4_day's base and patching it. the location HIJACKS the dormant "Suburbs"
# slot (SPT's Locations record is closed; suburbs is a shipped stub) — Id stays
# "Suburbs" so every native server lookup resolves; the display name comes from a
# locale entry the server mod adds. v1 = walkable milestone: bots fully disabled.
import json, os, re, glob

FACTORY = r"D:/SPTDev/SPT/SPT_Data/database/locations/factory4_day/base.json"
SUBURBS = r"D:/SPTDev/SPT/SPT_Data/database/locations/suburbs/base.json"
OUT = r"C:/Users/peard/Desktop/IcebreakerBundleOut/base.json"
SCENES = r"C:/Users/peard/Desktop/WTT-SDK-2022 Public/Assets/Icebreaker_Import/Content/Locations/Icebreaker/*.unity"


def extract_spawns():
    # our authored SpawnPointMarkers stored SpawnPoint.Position + Categories in-scene.
    # the server SpawnPointParams is the source of truth: EFT DELETES scene markers not in
    # this list and CREATES markers for entries not in the scene, so we regenerate params
    # from the authored positions. cat 1 = Player (PMC), cat 2 = Bot.
    out = []
    seen = set()
    for s in glob.glob(SCENES):
        txt = open(s, encoding="utf-8", errors="ignore").read()
        for m in re.finditer(r"Infiltration:\s*Icebreaker", txt):
            seg = txt[max(0, m.start() - 600):m.start()]
            pm = list(re.finditer(r"Position:\s*\{x:\s*([-\d.eE]+),\s*y:\s*([-\d.eE]+),\s*z:\s*([-\d.eE]+)\}", seg))
            cm = re.search(r"Categories:\s*(\d+)", seg)
            # the marker's authored SpawnPoint.Id — server params MUST reuse it, or the
            # merge deletes the scene marker and the BotZone list dangles on dead objects
            im = list(re.finditer(r"Id:\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", seg))
            if not pm or not im:
                continue
            x, y, z = (float(v) for v in pm[-1].groups())
            key = (round(x, 1), round(y, 1), round(z, 1))
            if key in seen:
                continue
            seen.add(key)
            is_player = (cm.group(1) if cm else "1") == "1"
            out.append((x, y, z, is_player, im[-1].group(1)))
    return out


def make_spawn_params(spawns):
    params = []
    for x, y, z, is_player, marker_id in spawns:
        params.append({
            "BotZoneName": "" if is_player else "ZoneIcebreaker",
            "Categories": ["Player"] if is_player else ["Bot"],
            "ColliderParams": {"_parent": "SpawnSphereParams",
                               "_props": {"Center": {"x": 0, "y": 0, "z": 0}, "Radius": 4}},
            "CorePointId": 0,
            "DelayToCanSpawnSec": 4,
            "Id": marker_id,  # matches the scene marker so the merge keeps it
            "Infiltration": "Icebreaker",
            "Position": {"x": x, "y": y, "z": z},
            "Rotation": 0,
            "Sides": ["All"],
        })
    return params


# retail-zone manifest written by the editor pass "Author 6 - Resurrect Retail BotZones":
# exact marker ids + per-zone BotZoneName for the 19 retail zones. when present, bot
# params come from here (zone-accurate); the legacy regex path only supplies PMC spawns.
MANIFEST = r"C:/Users/peard/Desktop/IcebreakerBundleOut/spawn_manifest.json"

def manifest_bot_params():
    if not os.path.exists(MANIFEST):
        return None, []
    mf = json.load(open(MANIFEST, encoding="utf-8"))
    params, zones = [], []
    for b in mf.get("bots", []):
        if b["zone"] not in zones:
            zones.append(b["zone"])
        params.append({
            "BotZoneName": b["zone"],
            "Categories": ["Bot"],
            "ColliderParams": {"_parent": "SpawnSphereParams",
                               "_props": {"Center": {"x": 0, "y": 0, "z": 0}, "Radius": b.get("rad", 4)}},
            "CorePointId": 0,
            "DelayToCanSpawnSec": 4,
            "Id": b["id"],
            "Infiltration": "Icebreaker",
            "Position": {"x": b["x"], "y": b["y"], "z": b["z"]},
            "Rotation": 0,
            "Sides": ["All"],
        })
    return params, zones

base = json.load(open(FACTORY, encoding="utf-8"))
stub = json.load(open(SUBURBS, encoding="utf-8"))

# identity: keep the suburbs slot's ids so server lookups + locale keys line up
base["_Id"] = stub["_Id"]
base["Id"] = "Suburbs"
base["Name"] = "Icebreaker"
base["Description"] = "The nuclear icebreaker BOREY, locked in arctic ice."
base["Enabled"] = True
base["Locked"] = False
base["IsSecret"] = False
base["DisabledForScav"] = True          # v1: PMC only
base["ForceOnlineRaidInPVE"] = False
base["AccessKeys"] = []
base["AccessKeysPvE"] = []
base["IconX"] = 350
base["IconY"] = 550

# scene loading: our patched retail preset, served by SPT's bundle system
base["Scene"] = {"path": "maps/icebreaker.bundle", "rcid": "icebreaker.scenespreset.asset"}

base["EscapeTimeLimit"] = 35
base["EscapeTimeLimitCoop"] = 35
# retail zones (from manifest) or the placeholder single zone
_bot_params, _zones = manifest_bot_params()
base["OpenZones"] = ",".join(_zones) if _zones else "ZoneIcebreaker"
base["OcculsionCullingEnabled"] = False  # we dropped the culling scene

# one exit, matching the authored scene exfil's Settings.Name exactly
# (exit_heli — the retail helicopter extraction trigger)
exit_template = dict(base["exits"][0])
exit_template.update({
    "Name": "Icebreaker_Exit_Heli",
    # MUST match the spawn markers' Infiltration — ExfiltrationPoint.EligibleEntryPoints
    # is EntryPoints.Split(','), and eligibility is Contains(player.EntryPoint): an empty
    # string means an empty list means NO player is ever eligible (the "flare fired but
    # exfil never opened" bug)
    "EntryPoints": "Icebreaker",
    "Chance": 100, "ChancePVE": 100,
    "ExfiltrationTime": 8, "ExfiltrationTimePVE": 8,
    "ExfiltrationType": "Individual",
    "PassageRequirement": "None",
    "MinTime": 0, "MaxTime": 0, "MinTimePVE": 0, "MaxTimePVE": 0,
    "PlayersCount": 0, "PlayersCountPVE": 0,
    "EventAvailable": False,
})
base["exits"] = [exit_template]

# milestone 2: bots ON — modest assault waves in ZoneIcebreaker. bots path on the
# surviving retail navmesh (rewired into Icebreaker_Scripts); covers/corepoints are
# empty until the generator injects them, so expect degraded combat AI at first.
BOTS = True
if BOTS:
    def wave(num, tmin, tmax, smin, smax, zone, wst="assault"):
        return {
            "number": num, "time_min": tmin, "time_max": tmax,
            "slots_min": smin, "slots_max": smax,
            "SpawnPoints": zone,
            "BotSide": "Savage", "BotPreset": "normal",
            "WildSpawnType": wst, "isPlayers": False,
            "sptId": f"icebreaker-wave-{num}",
        }
    if _zones:
        # retail composition (per user's retail knowledge): 9-15 rogues (exUsec), ALL
        # present from raid start (no trickle waves), + solo knight. ONLY rogues — no
        # generic scavs. black division comes later via the WTT mod dependency.
        # confirmed retail rogue zones (user-verified in the AI scene):
        ROGUE_ZONES = ["BotZoneKitchen", "BotZoneFront", "BotZoneFront2",
                       "BotZoneMash_t1", "BotZoneKorrDown1",
                       "BotZoneRoom_Eng", "BotZoneRoom_Eng2"]
        wave_zones = [z for z in ROGUE_ZONES if z in _zones]
        if not wave_zones:
            wave_zones = _zones[:5] if len(_zones) >= 5 else _zones
        base["waves"] = [
            wave(i, 0, 10, 2, 3, wz, "exUsec")   # all fire at raid start
            for i, wz in enumerate(wave_zones)
        ]  # 5 zones x 2-3 slots = 10-15 rogues, instantly
        # Knight, solo (no birdeye/bigpipe — escort amount 0), spawning among the rogues:
        # same zone pool as the waves, comma-separated = game picks one of them
        boss_zone = ",".join(wave_zones)
        base["BossLocationSpawn"] = [{
            "BossName": "bossKnight", "BossChance": 100, "BossZone": boss_zone,
            "BossPlayer": False, "BossDifficult": "normal",
            "BossEscortType": "followerBigPipe", "BossEscortDifficult": "normal",
            "BossEscortAmount": "0", "Time": -1,
            "TriggerId": "", "TriggerName": "", "Supports": None,
            "RandomTimeSpawn": False, "sptId": "icebreaker-knight",
        }]
        # NO server-side event waves: BossLocationSpawn with custom blackdiv roles is a
        # dead end in SPT 4.0 (triggers fired, waves silently never activated — verified
        # in-raid). the working hybrid: the client rebuilds the retail AIPlaceInfo trigger
        # boxes (IcebreakerAIPlaces.cs) which raise the retail events (hides/stern/wedges
        # group-size ladder + T1-T4 tiers), and IcebreakerCrew's event bridge delivers the
        # squads via the proven force-spawner. T1-T4 have no consumer yet (rogues are
        # capped 9-15 all-at-start per live — tiers are presumably aggro, TBD).
    else:
        base["waves"] = [wave(0, 20, 40, 1, 2, "ZoneIcebreaker"),
                         wave(1, 60, 120, 1, 2, "ZoneIcebreaker"),
                         wave(2, 300, 480, 1, 2, "ZoneIcebreaker")]
    base["BotMax"] = 30  # 10-15 rogues + knight + 12 black division
    base["BotStart"] = 1   # bot system live immediately — everyone spawns at raid start
    base["BotStop"] = 300
    base["BotMaxPlayer"] = 0
    base["BotSpawnCountStep"] = 0
    base["NewSpawn"] = False
    base["OldSpawn"] = True
    base["OfflineNewSpawn"] = False
    base["OfflineOldSpawn"] = True
else:
    base["waves"] = []
    base["BotMax"] = 0
    base["BotStart"] = 0
    base["BotStop"] = 0
    base["BotMaxPlayer"] = 0
    base["BotSpawnCountStep"] = 0
    base["NewSpawn"] = False
    base["OldSpawn"] = False
    base["OfflineNewSpawn"] = False
    base["OfflineOldSpawn"] = False
# factory template ships its own bosses — clear them unless the BOTS branch installed knight
if not (BOTS and _zones):
    base["BossLocationSpawn"] = []
base["MinMaxBots"] = []

# spawn selection: server params are the source of truth. with the retail-zone manifest,
# bot params come from it (exact ids + per-zone BotZoneName) and the scene regex supplies
# ONLY player spawns (the resurrected BornPositions markers would otherwise double-count).
spawns = extract_spawns()
if _bot_params:
    spawns = [s for s in spawns if s[3]]  # players only
    base["SpawnPointParams"] = make_spawn_params(spawns) + _bot_params
else:
    base["SpawnPointParams"] = make_spawn_params(spawns)
n_player = sum(1 for s in spawns if s[3])
base["MinDistToFreePoint"] = 0
base["MaxDistToFreePoint"] = 100
base["MinDistToExitPoint"] = 0
base["SpawnSafeDistanceMeters"] = 8

# no airdrops over the arctic (yet)
for a in base.get("airdropParameters", []):
    a["PlaneAirdropChance"] = 0

json.dump(base, open(OUT, "w", encoding="utf-8"), indent=2)
print(f"wrote {OUT}: Id={base['Id']} _Id={base['_Id']} Scene={base['Scene']}")
print(f"exits: {[e['Name'] for e in base['exits']]}, waves={len(base['waves'])}, bosses={len(base['BossLocationSpawn'])}, EscapeTimeLimit={base['EscapeTimeLimit']}")
print(f"SpawnPointParams: {len(base['SpawnPointParams'])} ({n_player} player, {len(base['SpawnPointParams'])-n_player} bot)")
print(f"OpenZones: {base['OpenZones']}")
