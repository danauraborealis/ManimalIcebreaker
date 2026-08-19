# BossWedge AI Report — for the Icebreaker custom-boss session

Source: WIP retail 1.0 `Assembly-CSharp.dll` (`G:\downloads\Assembly-CSharp.dll`, IL2CPP recovery,
decompiled with ilspycmd 10.1.1). Raw decompiled sources for every class named here are checked in at
[docs/wedge-ai-src/](wedge-ai-src/) — read those alongside this report; all line refs point into them.

**Decompile honesty:** this is Cpp2IL-recovered code. Field/const/enum values and class structure are
reliable; simple method bodies are readable; complex bodies are partial (`Decompilation failed` /
`Native no-return helper` throws mark them). Every snippet below is quoted from the actual output —
where a body is garbled I say what's inferable from the surviving fragments, not more.

Wedge does NOT appear in any of terminal's 9 captured wave lists — this brain belongs to some other
1.0 map/event. There is no parity target; this is blueprint material for a custom icebreaker boss.

---

## 1. Who Wedge is, mechanically

A cover-fighting ambush boss with **range-banded combat**, a **blood-triggered ambush relocate**, a
dedicated **indoor "Rooms" mode**, **stim usage**, **mine re-arming**, and squad coordination
(one-pusher-at-a-time throttling, fight requests, party-death cheat vision). The distinctive feel:
hit him and he disappears, then kills you from a shoot-cover point; fight him indoors and he cycles
rooms and stims when you deny him sight.

## 2. The boss core — `BossWedge` (wedge_BossWedge.cs, 1419 lines)

### 2.1 Range bands are computed by NAVMESH PATH LENGTH, not euclidean distance

```csharp
public enum EBossWedgeDist { close, mid, far }   // wedge_EBossWedgeDist.cs

// wedge_BossWedge.cs:66-72
private const float CHEAT_VISION_DIST = 30f;
private const float CLOSE_DIST = 12f;
private const float MID_DIST = 27f;
private const int DEAD_NEED_TO_CHEAT_VISION = -1;
```

`CalcDistByPath(EnemyInfo)` (line ~688) runs `NavMesh.CalculatePath` to the enemy and measures
`NavMeshPathExtension.CalculatePathLength(_path.corners)` (line ~660-664 shows the path-length
branch clearly). The band thresholds are 12m/27m of *walking distance* — an enemy 8m away through a
wall can still be "far". Band changes go through `SetState(EBossWedgeDist next)` (line 790) and a
3s periodic (`CheckDistPeriod`, wired in `SetPatrolMode`). This is the master switch for which
combat layer engages (§3).

### 2.2 Patrol mode

```csharp
// wedge_BossWedge.cs:328-336 (SetPatrolMode) — verbatim except IL noise removed
var chooser = PatrollingData.GetPointChooser(_owner, PatrolMode.oneByOne, owner.SpawnProfileData);
owner.PatrollingData.SetMode(PatrolMode.bossCoverScouts, chooser);
_checkDist = new AIPeriodAction(3f, CheckDistPeriod);
```

`PatrolMode.bossCoverScouts` is a vanilla patrol mode — the boss scouts cover-to-cover while idle.
Both symbols exist in SPT 4.0's assembly, so this line ports as-is.

### 2.3 Party-death cheat vision

Fields `_myPartyDead` + `DEAD_NEED_TO_CHEAT_VISION = -1` + `CHEAT_VISION_DIST = 30`. The body at
lines ~267-320: when the party-dead condition passes, it iterates `GameWorld.AllAlivePlayersList`,
filters `owner.EnemiesController.IsEnemy(player)`, and calls
`owner.BotsGroup.SetEnemyPos(player, enemyPos, weaponRootLast, isVisibleOnlyBySense)` — i.e. **when
his guys die, the group is fed enemy positions within 30m without line of sight**. Same
"you can't quietly whittle the squad" idea as vsRF's `DISTANCE_TO_CHEAT_VISIBILITY = 109`, but
death-triggered and tighter.

### 2.4 Mine re-arming

```csharp
// wedge_BossWedge.cs:90-92
private bool _isRewarmMinePlanted;
private const float SDIST_SAFE_REWARM_MINE = 81f;   // squared -> 9m safety radius
```

`PlantOneRandomRewarmMine()` (line 323, body failed to decompile) is called from the same
party-death block (line 271). Read: on losing party members, Wedge re-arms one random map mine —
the map carries `AMinePlace_*` GameObjects (one surfaced in terminal's level635 as
`AMinePlace_21700`, so the marker convention is `AMinePlace_<id>`). The 9m check keeps him from
arming a mine under his own feet. **Map-side requirement if you want this: authored mine-place
markers.**

### 2.5 Squad push throttling & help

```csharp
// wedge_BossWedge.cs:346-380 (signatures, bodies partial)
public void RegisterAttackMovingFlankToPointStarted(BotOwner starter)
public bool ShallHoldBecauseRecentGroupGoToEnemy(BotOwner requestingBot)
public void RegisterGoToEnemyStarted(BotOwner starter)
public bool WannaHelp()          // line 588
```

Followers ask the boss before pushing; if someone recently started a go-to-enemy or flank, the next
requester is told to hold. **One pusher at a time, rest hold cover** — cheap to imitate and very
visible in play.

### 2.6 Look/hold cadence

`PeriodHold` / `AmbushPossible` public bools (lines 108-131), `WannaChangeLook(bool
isShootFromCover)` (1369), `ChangeHold(float nextHold, float nextLook)` (1391) — the boss owns a
global "hold vs re-look" timer his layers consult (`BaseWedgeLayer.LookOrHold(keyWork, nextHold,
nextLook)` — wedge_BaseWedgeLayer.cs:66). It's what makes him feel patient rather than twitchy.

### 2.7 Constructor wiring

`BossWedge(BotOwner owner, BotBoss bossLogic)` builds a `List<AIPlaceInfo>` (line ~133) — he's
handed the map's AIPlaceInfo volumes at birth (used by StopAmbush + Rooms, below).

## 3. The brain — `BossWedgeLayersStrategy` (wedge_BossWedgeLayersStrategy.cs)

The full stack, verbatim priorities from the ctor (lines 37-102):

| prio | layer | note |
|-----:|-------|------|
| 140 | `WedgeGrenadeLayer` | distance-gated: `PeriodicCheck(IsGoodDist, 3f)` |
| 130 | `AvoidDangerLayer` | vanilla |
| 120 | `MalfunctionLayer` | vanilla |
| 90 | `WedgeFightRequest` | help-calls; `BossFinder<BossWedge>` (wedge_WedgeFightRequest.cs — 26-line thin subclass of vanilla `FightRequestLayer`) |
| 87 | `BossWedgeAmbush` | **blood-triggered** — see §4 |
| 82 | `WedgeRooms` | **active only when spawn zone name contains "Rooms"** — see §5 |
| 80 | `WedgeFarDist` | disabled in Rooms mode; stims (`AIPeriodAction(3f, TryUsingStims)`) |
| 70 | `WedgeMidDist` | disabled in Rooms mode; suppress + flank |
| 65 | `WedgeCloseDist` | always on; dogfight + taunts |
| 40 | `WedgeTargetLayer` | cover-in-middle hold (same shape as VSRFTargetLayer) |
| 2 | `PatrolAssaultLayer` | vanilla patrol |

The Rooms gate, verbatim (lines 68-79):

```csharp
string nameZone = owner.SpawnBotZone.NameZone;
bool isRooms = nameZone.Contains("Rooms", StringComparison.OrdinalIgnoreCase);
...
TryAddLayer(19, wedgeRooms, isRooms);          // Rooms layer only in *Rooms* zones
TryAddLayer(4, wedgeFarDist, !isRooms);        // far band disabled indoors
TryAddLayer(6, wedgeMidDist, !isRooms);        // mid band disabled indoors
TryAddLayer(7, wedgeCloseDist, activeOnStart: true);   // close always lives
```

**Icebreaker takeaway: the indoor/outdoor split is driven purely by the spawn zone's NAME.** Name a
BotZone `...Rooms...` and this boss becomes a CQB fighter for the whole raid.

## 4. Blood-triggered ambush — `BossWedgeAmbush` (wedge_BossWedgeAmbush.cs, prio 87)

Wiring in the strategy ctor (lines 61-67):

```csharp
owner.GetPlayer.BeingHitAction += bossWedgeAmbush.OnGetHit;   // subscribes to HIS OWN hits
```

```csharp
// wedge_BossWedgeAmbush.cs:74-100
private bool _isGetHitted;
private void OnGetHit(DamageInfo arg1, EBodyPart arg2, float arg3)   // sets the flag
private static bool IsShootCoverPoint(GroupPoint point)              // relocate filter
```

`ShallUseNow` (line 164, partial) keys on `_isGetHitted` + the boss's `AmbushPossible`. Effect:
**taking a hit makes him break contact and relocate to a shoot-cover point**, then wait. The map
can veto it: `AIPlaceInfoWedgeStopAmbush` volumes (wedge_AIPlaceInfoWedgeStopAmbush.cs — an empty
marker subclass) with `STOP_AMBUSH_RADIUS = 50f` on the boss core — inside 50m of a stop-ambush
marker, no ambushing (use these around spawn/exit areas where a camping boss would be degenerate).

## 5. Indoor mode — `WedgeRooms` (wedge_WedgeRooms.cs, 350 lines, prio 82)

```csharp
// wedge_WedgeRooms.cs:7-23
private const float STIM_COOLDOWN_SEC = 30f;
private const float STIM_NEED_NO_PERSONAL_SIGHT_SEC = 15f;
public const float STEP_AWAY_DIST = 5f;
private int _curPlaceId = -1;
private float _nextCanGoRound;
```

Room-to-room cover cycling: tracks the current place id, refreshes cover periodically
(`_nextRefreshCover` / `_lastChangeCover`), keeps 5m step-away spacing, and a `_nextCanGoRound`
timer gates "going around" (working the flank inside the room set). Stim rule: **if he hasn't
personally seen the enemy for 15s, he stims (30s cooldown)** — sustain pressure through denial.
`GetDecision` (line 25) and `FindPoint` (157) bodies are partial but the shape is a
place-scoped version of the cover search every other layer uses.

## 6. The range-band layers

All extend `BaseWedgeLayer` (wedge_BaseWedgeLayer.cs) which carries the shared machinery:

```csharp
// wedge_BaseWedgeLayer.cs:19-27
protected const float ENEMY_FORGOT = 15f;         // enemy info staleness horizon
protected CustomNavigationPoint _coverInMiddle;   // the "cover in the middle" pattern again
private const float NEXT_COVER_DELTA = 7f;
protected bool GoodPoint(GroupPoint arg)          // shared cover filter
protected AICoreActionResult<...> LookOrHold(string keyWork, float nextHold, float nextLook)
protected bool TryChangeSectorOrHold(out ...)     // sector-based repositioning
```

- **`WedgeFarDist`** (72 lines): thin — hold/look at range + `TryUsingStims` on the 3s periodic.
- **`WedgeMidDist`** (307 lines): the meat. Suppression state machine (`_possibleSuppress`,
  `_startSuppress`, `EndSuppressFromCover` line 230, `EndSuppressFire` 259) **plus flanking**
  (`EndAttackMovingFlank` line 284). Mid-range Wedge suppresses from cover and flanks — this is
  where `BossWedgeFlankNavMetrics` (207 lines, a navmesh-metric flank path evaluator) gets used.
- **`WedgeCloseDist`** (198 lines): dogfight mode within 5m:

```csharp
// wedge_WedgeCloseDist.cs:10-25
private AIPeriodAction _provocationPeriod;
private bool _holdOrDF;                    // hold vs dogfight
private float _endDogFight;
private const float DF_DIST = 5f;
private List<EPhraseTrigger> _tgriggers;   // [sic]
private void Provocation()                 // voice taunts at close range
```

  **He taunts you** — `Provocation()` fires `EPhraseTrigger` voice lines on a periodic while
  holding at close range. Cheap, high-flavor port (`bot.BotTalk.TrySay`).

## 7. Map-side requirements (authoring checklist for icebreaker)

1. **Zone naming**: a BotZone whose name contains `Rooms` ⇒ permanent CQB mode for the boss
   spawned there. Decide per encounter site.
2. **`AIPlaceInfoWedgeStopAmbush` volumes** (or your own marker equivalent): no-ambush bubbles,
   50m radius semantics, around anywhere a camping boss would be unfair.
3. **`AMinePlace_*` markers** if you want the mine re-arm flavor (needs the mine interactables too).
4. Baked cover (AI bake) everywhere he's meant to fight — every layer is cover-driven; icebreaker's
   restored bake already provides this.

## 8. Port plan mapped to our machinery (terminal precedents)

We've already built the exact patterns this needs, on terminal:

| Wedge piece | Terminal precedent to copy | Effort |
|---|---|---|
| Range bands by navmesh path (12/27m) + per-band posture | new logic in a `CustomLayer`; `NavMesh.CalculatePath` + `CalculatePathLength` are plain Unity | S |
| Blood-triggered ambush relocate | `TerminalRuafDefenseLayer` shape (activate on condition, claim shoot-cover via `Covers.FindClosestPoint` w/ filter, yield when enemy visible); subscribe `GetPlayer.BeingHitAction` exactly like retail | M |
| Rooms mode (indoor cover cycling + stim rule) | `TerminalSiegeLogic` (claimed-cover rotation) + `Medecine`/stims API; gate on spawn zone name | M |
| One-pusher-at-a-time squad throttling | static per-group registry, same pattern as `TerminalCrewJobs.ByProfile` | S |
| Close-range taunts | `BotTalk.TrySay(EPhraseTrigger...)` on a periodic in the close band | S |
| Party-death cheat vision | on group-member death, `BotsGroup.SetEnemyPos(...)` for enemies within 30m — API verified present in 4.0 (used by vsRF path too) | S |
| Suppress-from-cover + flank (mid band) | hardest; vanilla suppression exists per-bot — squad-sync version is the risky part. Recommend LAST or skip round 1 | L |
| Mine re-arming | needs map mine markers + interactable mines; flavor, defer | L |

**Round-1 recommendation** (one session, high visible impact, low combat-replacement risk):
bands + blood-ambush + taunts + push throttling, with the standing rule from the terminal ports:
**never replace visible-enemy gunfighting — activate custom logic only in dead air, yield the
instant the enemy is visible.** That rule is what kept the RUAF defense layer safe and it applies
double to a boss.

## 9. Decompile caveats (so nobody chases ghosts)

- Bodies that throw `Decompilation failed` / `Native no-return helper` in the sources are
  IL2CPP-recovery gaps, not real code — trust signatures/consts there, not control flow.
- `WedgeMidDist.ShallUseNow`, `WedgeRooms.GetDecision/FindPoint`, `PlantOneRandomRewarmMine`,
  `CopyData` are among the partial ones.
- The `12f == _lastPath` comparisons visible in `CheckDistPeriod` are decompiler artifacts of the
  band threshold compare (12/27) — read them as `<=` band checks, not equality.
- `LowEdgeHealth` / `ILowEdgeHealth` matched the "wedge" string scan but are unrelated
  (low-EDGE-health) — ignore.
