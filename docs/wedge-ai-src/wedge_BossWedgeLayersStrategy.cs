using System;
using EFT;
using EFT.Ballistics;

public class BossWedgeLayersStrategy : BaseBrain
{
	private const int ENEMY_LOGIC = 1;

	private const int PATROL_LOGIC = 2;

	private const int TARGET = 3;

	private const int FAR_DIST = 4;

	private const int GRENADE_DANGER = 5;

	private const int GRENADE_DANGER_2 = 11;

	private const int ROOMS = 19;

	private const int MID_DIST = 6;

	private const int CLOSE_DIST = 7;

	private const int MALFUNCTION_LAYER = 12;

	private const int FIGHT_REQUEST = 18;

	private const int BOSS_WEDGE_AMBUSH = 17;

	private float _lastChange = float.MinValue;

	public unsafe BossWedgeLayersStrategy(BotOwner owner)
		: base(owner)
	{
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Expected O, but got Unknown
		WedgeGrenadeLayer wedgeGrenadeLayer = null;
		((BaseWedgeLayer)wedgeGrenadeLayer)..ctor(owner, 140);
		Func<bool> func = null;
		((WedgeGrenadeLayer)(object)func).IsGoodDist();
		PeriodicCheck periodicCheck = null;
		periodicCheck..ctor(func, 3f);
		WedgeGrenadeLayer wedgeGrenadeLayer2 = (WedgeGrenadeLayer)(wedgeGrenadeLayer + 168L);
		wedgeGrenadeLayer._distCheck = periodicCheck;
		bool flag = wedgeGrenadeLayer2.IsGoodDist();
		bool flag2 = TryAddLayer(11, wedgeGrenadeLayer, activeOnStart: true);
		AvoidDangerLayer avoidDangerLayer = null;
		avoidDangerLayer..ctor(owner, 130);
		bool flag3 = TryAddLayer(5, avoidDangerLayer, activeOnStart: true);
		MalfunctionLayer malfunctionLayer = null;
		malfunctionLayer..ctor(owner, 120);
		bool flag4 = TryAddLayer(12, malfunctionLayer, activeOnStart: true);
		WedgeFightRequest wedgeFightRequest = null;
		((FightRequestLayer)wedgeFightRequest)..ctor(owner, 90);
		BossFinder<BossWedge> bossFinder = null;
		bossFinder..ctor(owner);
		wedgeFightRequest._boss = bossFinder;
		wedgeFightRequest._boss.FindBoss();
		bool flag5 = TryAddLayer(18, wedgeFightRequest, activeOnStart: true);
		BossWedgeAmbush bossWedgeAmbush = null;
		((BaseWedgeLayer)bossWedgeAmbush)..ctor(owner, 87);
		BotOwner owner2 = ((BaseLogicLayerSimple)bossWedgeAmbush)._owner;
		Action<DamageInfo, EBodyPart, float> action = null;
		action..ctor((object)bossWedgeAmbush, (IntPtr)(nint)__ldftn(BossWedgeAmbush.OnGetHit));
		owner2.<GetPlayer>k__BackingField.BeingHitAction += action;
		bool flag6 = TryAddLayer(17, bossWedgeAmbush, activeOnStart: true);
		BotOwner owner3 = _owner;
		string nameZone = owner3.<SpawnBotZone>k__BackingField.NameZone;
		bool flag7 = nameZone.Contains("Rooms", StringComparison.OrdinalIgnoreCase);
		AILogger instance = AILogger.Instance;
		object arg = null;
		string format = $"BossWedgeLayersStrategy init rooms:{arg}";
		object[] args = Array.Empty<object>();
		instance.LogTrace(format, args);
		WedgeRooms wedgeRooms = null;
		wedgeRooms._curPlaceId = -1;
		((BaseWedgeLayer)wedgeRooms)..ctor(owner, 82);
		bool flag8 = TryAddLayer(19, wedgeRooms, flag7);
		WedgeFarDist wedgeFarDist = null;
		((BaseWedgeLayer)wedgeFarDist)..ctor(owner, 80);
		Action action2 = null;
		action2..ctor(wedgeFarDist, (nint)__ldftn(WedgeFarDist.TryUsingStims));
		AIPeriodAction aIPeriodAction = null;
		aIPeriodAction..ctor(3f, action2);
		wedgeFarDist._usingStims = aIPeriodAction;
		int activeOnStart = (flag7 ? 1 : 0) ^ 1;
		bool flag9 = TryAddLayer(4, wedgeFarDist, (byte)activeOnStart != 0);
		WedgeMidDist wedgeMidDist = null;
		((BaseWedgeLayer)wedgeMidDist)..ctor(owner, 70);
		int activeOnStart2 = (flag7 ? 1 : 0) ^ 1;
		bool flag10 = TryAddLayer(6, wedgeMidDist, (byte)activeOnStart2 != 0);
		WedgeCloseDist wedgeCloseDist = null;
		wedgeCloseDist..ctor(owner, 65);
		bool flag11 = TryAddLayer(7, wedgeCloseDist, activeOnStart: true);
		WedgeTargetLayer wedgeTargetLayer = null;
		((BaseWedgeLayer)wedgeTargetLayer)..ctor(owner, 40);
		bool flag12 = TryAddLayer(3, wedgeTargetLayer, activeOnStart: true);
		PatrolAssaultLayer patrolAssaultLayer = null;
		patrolAssaultLayer..ctor(owner, 2);
		bool flag13 = TryAddLayer(2, patrolAssaultLayer, activeOnStart: true);
	}

	public override string ShortName()
	{
		return "Wedge";
	}

	protected override BotEventsPriority EventsPriority()
	{
		BotEventsPriority botEventsPriority = null;
		int followPlayer = default(int);
		int halloweenHide = default(int);
		int gotoGenerator = default(int);
		int goToTarget = default(int);
		botEventsPriority..ctor(-1, -1, -1, followPlayer, halloweenHide, gotoGenerator, goToTarget);
		return botEventsPriority;
	}
}
