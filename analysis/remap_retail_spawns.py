# RETAIL SPAWN TABLE -> OUR base.json (2026-08-11): retire the custom crew spawner and
# hand the whole choreography back to BSG's own wave generator, the way terminal does it.
#
# two halves get replaced, nothing else in our base.json is touched:
#   1. SpawnPointParams — ours were 26 invented points in one made-up "ZoneIcebreaker",
#      which is WHY a custom spawner was needed: a BossLocationSpawn row naming
#      BotZoneEngineHide had nowhere to put anyone. retail ships 53 bot points across
#      the 20 real zones. our 3 Player (infil) points are ours and stay.
#   2. BossLocationSpawn — retail's 40 rows, with the black division roles remapped to
#      the one role the BlackDiv mod gives us.
#
# role map: retail splits BD into three roles we don't have; all collapse to blackDivIb.
#   bossBullyBlackDiv / followerBullyBlackDiv / pmcBotBlackDiv -> blackDivIb
#   bossKnight, exUsec  -> unchanged (vanilla roles)
#   bossWedge           -> SPECIAL, see below
#
# the wedge trap: retail's bossWedge is a GENERIC squad role, so they happily run three
# bossWedge rows per trigger (one per room). the BlackDiv mod's bossWedge is THE named
# boss with unique gear — three rows meant three Wedges (the 08-08 test raid). so per
# wedges trigger exactly ONE row stays bossWedge (the row that carried the most escorts,
# but with a comma zone list so BornZone randomises which of the three rooms he holds),
# and the other two rows become plain blackDivIb squads in their authored rooms.
# headcount per trigger is preserved exactly.
import json, collections

RETAIL = r"G:\downloads\base(1).json"
OURS = r"C:\Users\peard\Desktop\ManimalIcebreaker\icebreaker-server\db\base.json"

BD = "blackDivIb"
BD_ROLES = {"bossBullyBlackDiv", "followerBullyBlackDiv", "pmcBotBlackDiv"}
WEDGE_ROOMS = "BotZoneRoomsFour,BotZoneRoomsThird,BotZoneRoomsThirdKitchen"

retail = json.load(open(RETAIL, encoding="utf-8"))
ours = json.load(open(OURS, encoding="utf-8"))

# ---- 1. spawn points -------------------------------------------------------
ours_player = [s for s in ours["SpawnPointParams"] if "Player" in (s.get("Categories") or [])]
retail_bot = [s for s in retail["SpawnPointParams"] if "Player" not in (s.get("Categories") or [])]
new_points = ours_player + retail_bot

# ---- 2. boss rows ----------------------------------------------------------
rows = [dict(r) for r in retail["BossLocationSpawn"]]

# wedge rows grouped by trigger so we can elect exactly one real boss per trigger
wedge_by_trigger = collections.defaultdict(list)
for r in rows:
    if r["BossName"] == "bossWedge":
        wedge_by_trigger[r["TriggerId"]].append(r)
wedge_keep = set()
for trig, group in wedge_by_trigger.items():
    # the row carrying the most escorts is the "real" boss row
    boss_row = max(group, key=lambda r: int(r["BossEscortAmount"] or 0))
    wedge_keep.add(id(boss_row))

out = []
for r in rows:
    if r["BossName"] == "bossWedge":
        if id(r) in wedge_keep:
            r["BossZone"] = WEDGE_ROOMS       # random room per raid
            r["BossEscortType"] = BD
            r["BossDifficult"] = "normal"     # a named boss, not one of three grunts
            r["BossEscortDifficult"] = "normal"
        else:
            r["BossName"] = BD                # the other two rooms get BD squads
            r["BossEscortType"] = BD
            r["BossDifficult"] = "normal"
            r["BossEscortDifficult"] = "normal"
    else:
        if r["BossName"] in BD_ROLES:
            r["BossName"] = BD
        if r["BossEscortType"] in BD_ROLES:
            r["BossEscortType"] = BD
    out.append(r)

ours["SpawnPointParams"] = new_points
ours["BossLocationSpawn"] = out
ours["waves"] = []  # user call: no regular scav waves on this map

json.dump(ours, open(OURS, "w", encoding="utf-8"), indent=2, ensure_ascii=False)

# ---- report ----------------------------------------------------------------
print(f"spawn points: {len(ours_player)} ours(player) + {len(retail_bot)} retail(bot) = {len(new_points)}")
zc = collections.Counter(s.get("BotZoneName") for s in retail_bot)
print("  zones:", len(zc))
print(f"boss rows: {len(out)}")
per = collections.defaultdict(lambda: collections.Counter())
for r in out:
    trig = r["TriggerId"] or "<raid start>"
    n = 1 + int(r["BossEscortAmount"] or 0)
    per[trig][r["BossName"]] += n
for trig in sorted(per, key=lambda t: (t == "<raid start>", t)):
    tot = sum(per[trig].values())
    detail = ", ".join(f"{v}x {k}" for k, v in per[trig].items())
    chance = {r["BossChance"] for r in out if (r["TriggerId"] or "<raid start>") == trig}
    print(f"  {trig:<12} {tot:>3} bots  ({detail})  chance={sorted(chance)}")
