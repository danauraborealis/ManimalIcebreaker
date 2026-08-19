using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.AI;

public class BossWedge : ABossLogic
{
	[Serializable]
	private sealed class <>c
	{
		public static readonly <>c <>9;

		public static Func<AIPlaceInfo, bool> <>9__27_1;

		public static Func<AIPlaceInfoWedgeStopAmbush, string> <>9__39_0;

		static <>c()
		{
			<>c <>c2 = null;
			<>9 = <>c2;
		}

		internal bool <.ctor>b__27_1(AIPlaceInfo x)
		{
			bool flag = x == null;
			AIPlaceInfo aIPlaceInfo = null;
			if (!flag)
			{
				bool flag2 = x is AIPlaceInfoLogicWedge;
				AIPlaceInfo aIPlaceInfo3 = default(AIPlaceInfo);
				AIPlaceInfo aIPlaceInfo2 = aIPlaceInfo3;
				if (!flag2)
				{
					aIPlaceInfo2 = null;
				}
				bool flag3 = aIPlaceInfo2 == null;
				aIPlaceInfo = null;
				if (!flag3)
				{
					aIPlaceInfo = x;
				}
			}
			AIPlaceInfo aIPlaceInfo4 = (AIPlaceInfo)((object)aIPlaceInfo - (object)null);
			bool flag4 = aIPlaceInfo4 == null;
			return !flag4;
		}

		internal string <InitStopAmbushSubscriptions>b__39_0(AIPlaceInfoWedgeStopAmbush z)
		{
			GameObject gameObject = ((Component)z).gameObject;
			return ((Object)gameObject).name;
		}
	}

	private EBossWedgeDist <WedgeDist>k__BackingField;

	private bool <PeriodHold>k__BackingField;

	private bool <AmbushPossible>k__BackingField;

	private AIPeriodAction _periodCheckVision;

	private const float CHEAT_VISION_DIST = 30f;

	private const float CLOSE_DIST = 12f;

	private const float MID_DIST = 27f;

	private const int DEAD_NEED_TO_CHEAT_VISION = -1;

	private AIPeriodAction _checkDist;

	private List<AIPlaceInfo> _allWedgeZones;

	private readonly List<AIPlaceInfoWedgeStopAmbush> _stopAmbushPlaces;

	private const float STOP_AMBUSH_RADIUS = 50f;

	private NavMeshPath _path;

	private float _lastPath;

	private int _myPartyDead;

	private readonly BossWedgeFlankNavMetrics _flankNavMetrics;

	private bool _isRewarmMinePlanted;

	private const float SDIST_SAFE_REWARM_MINE = 81f;

	private float _nextChangeLook;

	public EBossWedgeDist WedgeDist
	{
		get
		{
			return <WedgeDist>k__BackingField;
		}
		private set
		{
			<WedgeDist>k__BackingField = value;
		}
	}

	public bool PeriodHold
	{
		get
		{
			return <PeriodHold>k__BackingField;
		}
		set
		{
			<PeriodHold>k__BackingField = value;
		}
	}

	public bool AmbushPossible
	{
		get
		{
			return <AmbushPossible>k__BackingField;
		}
		set
		{
			<AmbushPossible>k__BackingField = value;
		}
	}

	public unsafe BossWedge(BotOwner owner, BotBoss bossLogic)
	{
		//IL_01f7->IL0197: Incompatible stack heights: 2 vs 1
		List<AIPlaceInfo> list = null;
		list..ctor();
		_allWedgeZones = list;
		List<AIPlaceInfoWedgeStopAmbush> list2 = null;
		list2..ctor();
		_stopAmbushPlaces = list2;
		NavMeshPath val = null;
		val..ctor();
		_path = val;
		BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics = null;
		NavMeshPath val2 = null;
		val2..ctor();
		bossWedgeFlankNavMetrics._navPathBuffer = val2;
		bossWedgeFlankNavMetrics._pathToEnemyCacheTime = float.NegativeInfinity;
		bossWedgeFlankNavMetrics._pathToEnemyCacheBotId = -1;
		bossWedgeFlankNavMetrics._lastFlankToPointStartTime = float.NegativeInfinity;
		bossWedgeFlankNavMetrics._lastGoToEnemyStartTime = float.NegativeInfinity;
		_flankNavMetrics = bossWedgeFlankNavMetrics;
		base..ctor(owner, bossLogic);
		BotOwner owner2 = _owner;
		BotsController <BotsController>k__BackingField = owner2.<BotsController>k__BackingField;
		AICoversData coversData = <BotsController>k__BackingField._coversData;
		AIPlaceInfoHolder aIPlaceInfoHolder = coversData.AIPlaceInfoHolder;
		Action action = null;
		action..ctor(this, (nint)__ldftn(BossWedge.PeriodSetInfo));
		AIPeriodAction aIPeriodAction = null;
		aIPeriodAction..ctor(10f, action);
		_periodCheckVision = aIPeriodAction;
		Func<AIPlaceInfo, bool> func = null;
		((BossWedge)(object)func).<.ctor>b__27_0((AIPlaceInfo)(object)this);
		IEnumerable<object> source = aIPlaceInfoHolder.Places.Where(func);
		bool flag = <>c.<>9__27_1 != null;
		Func<AIPlaceInfo, bool> predicate = <>c.<>9__27_1;
		if (!flag)
		{
			Func<AIPlaceInfo, bool> func2 = null;
			((<>c)(object)func2).<.ctor>b__27_1((AIPlaceInfo)(object)<>c.<>9);
			<>c.<>9__27_1 = func2;
			predicate = func2;
		}
		IEnumerable<object> source2 = ((IEnumerable<AIPlaceInfo>)source).Where(predicate);
		List<object> allWedgeZones = source2.ToList();
		_allWedgeZones = (List<AIPlaceInfo>)(object)allWedgeZones;
		BotOwner owner3 = _owner;
		Action<BotOwner> action2 = null;
		((BossWedge)(object)action2).OnMemberRemove((BotOwner)(object)this);
		owner3.<BotsGroup>k__BackingField.OnMemberRemove += action2;
		List<AIPlaceInfo> allWedgeZones2 = _allWedgeZones;
		if (allWedgeZones2._size > 1L)
		{
			return;
		}
		List<object> list3 = source.ToList();
		if (_allWedgeZones != null)
		{
			object arg = null;
			BotOwner owner4 = _owner;
			if (owner4.<StartCorePoint>k__BackingField != null)
			{
				object arg2 = null;
				if (list3 != null)
				{
					object arg3 = null;
					string format = $"Boss wedge ai place error: low count of AIPlaceInfoLogicWedge: {arg} ownerCG:{arg2} myCG:{arg3}";
					AILogger instance = AILogger.Instance;
					object[] args = Array.Empty<object>();
					instance.LogWarn(format, args);
					return;
				}
			}
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	private unsafe void OnMemberRemove(BotOwner obj)
	{
		//IL_0107: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Expected O, but got Unknown
		//IL_011b: Expected O, but got I4
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Expected O, but got Unknown
		//IL_0170: Expected O, but got I4
		BossWedgeFlankNavMetrics flankNavMetrics = _flankNavMetrics;
		if (!((Object)(object)obj == (Object)null))
		{
			if (obj.<Id>k__BackingField == flankNavMetrics._pathToEnemyCacheBotId)
			{
				flankNavMetrics._pathToEnemyCacheBotId = -1;
			}
			if ((Object)(object)obj == (Object)(object)flankNavMetrics._lastFlankToPointStarter)
			{
				BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics = (BossWedgeFlankNavMetrics)(flankNavMetrics + 48L);
				flankNavMetrics._lastFlankToPointStarter = null;
				object obj2 = 0;
			}
			if ((Object)(object)obj == (Object)(object)flankNavMetrics._lastGoToEnemyStarter)
			{
				BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics2 = (BossWedgeFlankNavMetrics)(flankNavMetrics + 56L);
				flankNavMetrics._lastGoToEnemyStarter = null;
				object obj3 = 0;
			}
		}
		BotOwner owner = _owner;
		BotsController <BotsController>k__BackingField = owner.<BotsController>k__BackingField;
		Action action = null;
		action..ctor(this, (nint)__ldftn(BossWedge.AddDelays));
		int num = <BotsController>k__BackingField.<AiTaskManager>k__BackingField.RegisterDelayedTask(owner, 1f, action);
	}

	private void AddDelays()
	{
		int myPartyDead = _myPartyDead + 1;
		_myPartyDead = myPartyDead;
	}

	private void PeriodSetInfo()
	{
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Expected F4, but got Unknown
		//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_020d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0212: Unknown result type (might be due to invalid IL or missing references)
		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
		//IL_0236: Unknown result type (might be due to invalid IL or missing references)
		//IL_0251: Unknown result type (might be due to invalid IL or missing references)
		//IL_0255: Unknown result type (might be due to invalid IL or missing references)
		if (_myPartyDead <= -1L)
		{
			return;
		}
		PlantOneRandomRewarmMine();
		GameWorld instance = Singleton<GameWorld>.Instance;
		if (instance.AllAlivePlayersList != null)
		{
			List<Player>.Enumerator enumerator = instance.AllAlivePlayersList.GetEnumerator();
			List<Player>.Enumerator enumerator2 = enumerator;
			List<object>.Enumerator enumerator3 = default(List<object>.Enumerator);
			Vector3 v = default(Vector3);
			Vector3 enemyPos = default(Vector3);
			Vector3 weaponRootLast = default(Vector3);
			EEnemyPartVisibleType isVisibleOnlyBySense = default(EEnemyPartVisibleType);
			while (true)
			{
				if (enumerator3.MoveNext())
				{
					BotOwner owner = _owner;
					if (!owner.<EnemiesController>k__BackingField.IsEnemy(enumerator._current))
					{
						continue;
					}
					Vector3 position = enumerator._current.Position;
					if (_owner == null)
					{
						break;
					}
					Vector3 position2 = _owner.Position;
					float num = position.y - position2.y;
					float num2 = 0x7FFFFFFF7FFFFFFFL & num;
					if (float.NaN == num2)
					{
						Vector3 position3 = enumerator._current.Position;
						float num3 = _owner.SDistTo(v);
						if (float.NaN != num3)
						{
							BotOwner owner2 = _owner;
							BifacialTransform transform = enumerator._current.Transform;
							Vector3 position4 = transform.position;
							BifacialTransform weaponRoot = enumerator._current.WeaponRoot;
							Vector3 position5 = weaponRoot.position;
							owner2.<BotsGroup>k__BackingField.SetEnemyPos(enumerator._current, enemyPos, weaponRootLast, isVisibleOnlyBySense);
						}
					}
					continue;
				}
				enumerator2.Dispose();
				return;
			}
			throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	private void PlantOneRandomRewarmMine()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v290 @ rax_v20+60]:8");
	}

	public unsafe override void SetPatrolMode()
	{
		BotOwner owner = _owner;
		PatrolPointChooserBasic pointChooser = PatrollingData.GetPointChooser(_owner, PatrolMode.oneByOne, owner.<SpawnProfileData>k__BackingField);
		BotOwner owner2 = _owner;
		owner2.<PatrollingData>k__BackingField.SetMode(PatrolMode.bossCoverScouts, pointChooser);
		Action action = null;
		action..ctor(this, (nint)__ldftn(BossWedge.CheckDistPeriod));
		AIPeriodAction aIPeriodAction = null;
		aIPeriodAction..ctor(3f, action);
		_checkDist = aIPeriodAction;
	}

	public override void CopyData(ABossLogic bossBossLogic)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [bossBossLogic @ rdx (ABossLogic)+54]:4");
	}

	public void RegisterAttackMovingFlankToPointStarted(BotOwner starter)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Expected O, but got Unknown
		//IL_004b: Expected O, but got I4
		BossWedgeFlankNavMetrics flankNavMetrics = _flankNavMetrics;
		float time = Time.time;
		BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics = (BossWedgeFlankNavMetrics)(_flankNavMetrics + 48L);
		flankNavMetrics._lastFlankToPointStartTime = time;
		flankNavMetrics._lastFlankToPointStarter = starter;
		object obj = 0;
	}

	public unsafe bool ShallDeferFlankBecauseRecentGroupFlank(BotOwner requestingBot, out BotLogicDecision substituteDecision)
	{
		BossWedgeFlankNavMetrics flankNavMetrics = _flankNavMetrics;
		ref BotLogicDecision reference = ref *(BotLogicDecision*)9L;
		if (!((Object)(object)flankNavMetrics._lastFlankToPointStarter == (Object)null))
		{
			float time = Time.time;
		}
		return false;
	}

	public bool ShallHoldBecauseRecentGroupGoToEnemy(BotOwner requestingBot)
	{
		BossWedgeFlankNavMetrics flankNavMetrics = _flankNavMetrics;
		if (!((Object)(object)flankNavMetrics._lastGoToEnemyStarter == (Object)null))
		{
			float time = Time.time;
		}
		return false;
	}

	public void RegisterGoToEnemyStarted(BotOwner starter)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Expected O, but got Unknown
		//IL_004b: Expected O, but got I4
		BossWedgeFlankNavMetrics flankNavMetrics = _flankNavMetrics;
		float time = Time.time;
		BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics = (BossWedgeFlankNavMetrics)(_flankNavMetrics + 56L);
		flankNavMetrics._lastGoToEnemyStartTime = time;
		flankNavMetrics._lastGoToEnemyStarter = starter;
		object obj = 0;
	}

	public override void Activate()
	{
		BotOwner owner = _owner;
		Action<BotOwner> action = null;
		((BossWedge)(object)action).OnGoalEnemyChanged((BotOwner)(object)this);
		owner.Memory.OnGoalEnemyChanged += action;
		InitStopAmbushSubscriptions();
		AILogger instance = AILogger.Instance;
		object[] array = new object[2];
		if (_owner != null)
		{
			object obj = null;
			array[0] = obj;
			object obj2 = null;
			array[1] = obj2;
			instance.LogTrace("BossWedge Activate botId:{0} ambushPossible:{1} (WedgeStopAmbush)", array);
			return;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	private void InitStopAmbushSubscriptions()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v244 @ rax_v42 (System.Collections.Generic.IEnumerable`1<System.Object>)+18]:4");
	}

	private static Vector3 GetStopAmbushPlaceCenter(AIPlaceInfoWedgeStopAmbush place)
	{
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Expected F4, but got Unknown
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Expected F4, but got Unknown
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		Vector3 result = default(Vector3);
		if (!((Object)(object)place.Collider != (Object)null))
		{
			Transform transform = ((Component)place).transform;
			Bounds position = (Bounds)transform.position;
			result.x = ((Vector3)position).x;
			result.z = ((Vector3)position).z;
			return result;
		}
		if (place.Collider != null)
		{
			Bounds position = ((Collider)place.Collider).bounds;
			result.x = (float)position.m_Center;
			result.z = (float)position.m_Center;
			return result;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	private unsafe void OnStopAmbushZonePlayerEnter(Player player)
	{
		//IL_00c5: Expected O, but got I
		//IL_01bf: Expected O, but got I4
		//IL_026c: Expected O, but got I4
		//IL_02ad: Expected O, but got I4
		//IL_02b2: Expected O, but got I4
		string text;
		AILogger instance2;
		object[] array;
		string text2;
		string text3;
		if (player.<AIData>k__BackingField != null)
		{
			bool flag = false;
			if (flag)
			{
				return;
			}
			<AmbushPossible>k__BackingField = flag;
			AILogger instance = AILogger.Instance;
			if (!instance.IsTraceEnable())
			{
				return;
			}
			if (_stopAmbushPlaces != null)
			{
				List<AIPlaceInfoWedgeStopAmbush>.Enumerator enumerator = _stopAmbushPlaces.GetEnumerator();
				List<AIPlaceInfoWedgeStopAmbush>.Enumerator enumerator2 = enumerator;
				object obj = (object)__ldftn(List<AIPlaceInfoWedgeStopAmbush>.GetEnumerator);
				List<object>.Enumerator enumerator3 = default(List<object>.Enumerator);
				while (true)
				{
					if (!enumerator3.MoveNext())
					{
						enumerator2.Dispose();
						text = "?";
						object obj2 = obj;
						break;
					}
					bool flag2 = enumerator._current.ContainsPlayer(player);
					bool flag3 = !flag2;
					obj = null;
					if (!flag3)
					{
						GameObject gameObject = ((Component)enumerator._current).gameObject;
						string name = ((Object)gameObject).name;
						enumerator2.Dispose();
						text = name;
						object obj2 = null;
						break;
					}
				}
				instance2 = AILogger.Instance;
				array = new object[4];
				object obj3 = null;
				array[0] = obj3;
				object obj4 = 0;
				object obj5 = null;
				array[1] = obj5;
				if (player.<Profile>k__BackingField != null)
				{
					string nickname = player.<Profile>k__BackingField.Nickname;
					bool flag4 = nickname != null;
					text2 = nickname;
					if (flag4)
					{
						goto IL_025f;
					}
				}
				bool flag5 = "" == null;
				text2 = "";
				text3 = "";
				if (!flag5)
				{
					goto IL_025f;
				}
				goto IL_0291;
			}
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
		IL_0291:
		array[2] = text3;
		array[3] = text;
		instance2.LogTrace("BossWedge ????? ????? ? ???? ?????? ?????? (WedgeStopAmbush) botId:{0} playerId:{1} playerNick:{2} zone:{3} ambushPossible->false", array);
		return;
		IL_025f:
		object obj6 = array;
		object obj7 = 0;
		bool flag6 = obj7 == null;
		text3 = text2;
		if (!flag6)
		{
			goto IL_0291;
		}
		object obj8 = 0;
		object obj9 = 0;
		throw new Exception("Native interrupt reached");
	}

	private void UnsubscribeStopAmbushPlaces()
	{
		List<AIPlaceInfoWedgeStopAmbush> stopAmbushPlaces = _stopAmbushPlaces;
		if (stopAmbushPlaces._size > 0)
		{
			AILogger instance = AILogger.Instance;
			object[] array = new object[2];
			object obj = null;
			array[0] = obj;
			object obj2 = null;
			array[1] = obj2;
			instance.LogTrace("BossWedge stop WedgeStopAmbush botId:{0} count:{1}", array);
		}
		if (_stopAmbushPlaces != null)
		{
			List<AIPlaceInfoWedgeStopAmbush>.Enumerator enumerator = _stopAmbushPlaces.GetEnumerator();
			List<AIPlaceInfoWedgeStopAmbush>.Enumerator enumerator2 = enumerator;
			List<object>.Enumerator enumerator3 = default(List<object>.Enumerator);
			while (enumerator3.MoveNext())
			{
				Action<Player> action = null;
				((BossWedge)(object)action).OnStopAmbushZonePlayerEnter((Player)(object)this);
				enumerator._current.OnPlayerEnter -= action;
			}
			enumerator2.Dispose();
			List<AIPlaceInfoWedgeStopAmbush> stopAmbushPlaces2 = _stopAmbushPlaces;
			int version = stopAmbushPlaces2._version + 1;
			stopAmbushPlaces2._version = version;
			stopAmbushPlaces2._size = 0;
			if (stopAmbushPlaces2._size > 0)
			{
				Array.Clear(stopAmbushPlaces2._items, 0, stopAmbushPlaces2._size);
			}
			return;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	public bool WannaHelp()
	{
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		if (memory._goalEnemy != null)
		{
			float time = Time.time;
			float num = time - goalEnemy.<PersonalLastSeenTime>k__BackingField;
			bool flag = 15f < num;
			bool flag2 = 15f == num;
			int num2 = ((!flag) ? 1 : 0);
			int num3 = ((!flag2) ? 1 : 0);
			return (byte)(num2 & num3) != 0;
		}
		return false;
	}

	private void OnGoalEnemyChanged(BotOwner obj)
	{
		CheckDistPeriod();
		_checkDist.ResetNextUpdate();
	}

	private void CheckDistPeriod()
	{
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Invalid comparison between Unknown and I4
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Expected O, but got I4
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		if (memory._goalEnemy == null)
		{
			return;
		}
		Vector3 position = _owner.Position;
		position = memory._goalEnemy.CurrPosition;
		position.x = position.x;
		Vector3 val = default(Vector3);
		Vector3 val2 = default(Vector3);
		bool flag = NavMesh.CalculatePath(val, val2, -1, _path);
		NavMeshPathStatus status = _path.status;
		float lastPath;
		if ((int)status != 0)
		{
			if (_owner == null)
			{
				throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
			}
			Vector3 position2 = _owner.Position;
			position2 = memory._goalEnemy.CurrPosition;
			float x = position2.x;
			object obj = 0;
			if (position2.x == _lastPath)
			{
				goto IL_0186;
			}
			lastPath = position2.x;
		}
		else
		{
			Vector3[] corners = _path.corners;
			float num = NavMeshPathExtension.CalculatePathLength(corners);
			lastPath = num;
		}
		_lastPath = lastPath;
		goto IL_0186;
		IL_0186:
		if (12f == _lastPath)
		{
			if (12f == _lastPath)
			{
				if ((long)<WedgeDist>k__BackingField != 2L)
				{
					<WedgeDist>k__BackingField = EBossWedgeDist.far;
				}
			}
			else if ((long)<WedgeDist>k__BackingField != 1L)
			{
				<WedgeDist>k__BackingField = EBossWedgeDist.mid;
			}
		}
		else if (<WedgeDist>k__BackingField != EBossWedgeDist.close)
		{
			<WedgeDist>k__BackingField = EBossWedgeDist.close;
		}
	}

	private void CalcDistByPath(EnemyInfo enemy)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Invalid comparison between Unknown and I4
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Expected O, but got I4
		if (_owner != null)
		{
			Vector3 position = _owner.Position;
			position = enemy.CurrPosition;
			position.x = position.x;
			Vector3 val = default(Vector3);
			Vector3 val2 = default(Vector3);
			bool flag = NavMesh.CalculatePath(val, val2, -1, _path);
			NavMeshPathStatus status = _path.status;
			float lastPath;
			if ((int)status != 0)
			{
				if (_owner == null)
				{
					goto IL_002f;
				}
				Vector3 position2 = _owner.Position;
				position2 = enemy.CurrPosition;
				float x = position2.x;
				object obj = 0;
				bool flag2 = position2.x == _lastPath;
				lastPath = position2.x;
				if (flag2)
				{
					return;
				}
			}
			else
			{
				Vector3[] corners = _path.corners;
				float num = NavMeshPathExtension.CalculatePathLength(corners);
				lastPath = num;
			}
			_lastPath = lastPath;
			return;
		}
		goto IL_002f;
		IL_002f:
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	public unsafe bool IsEnemyAtMyZone(IPlayer enemy, out AIPlaceInfo place)
	{
		//IL_003c: Expected O, but got I
		//IL_00c6: Expected O, but got I4
		//IL_0133: Expected O, but got I4
		if (_allWedgeZones != null)
		{
			List<AIPlaceInfo>.Enumerator enumerator = _allWedgeZones.GetEnumerator();
			List<AIPlaceInfo>.Enumerator enumerator2 = enumerator;
			object obj = (object)__ldftn(List<AIPlaceInfo>.GetEnumerator);
			List<object>.Enumerator enumerator3 = default(List<object>.Enumerator);
			ref AIPlaceInfo reference;
			while (true)
			{
				if (!enumerator3.MoveNext())
				{
					enumerator2.Dispose();
					reference = ref *(AIPlaceInfo*)0L;
					object obj2 = 0;
					return false;
				}
				bool flag = enumerator._current.ContainsPlayer(enemy);
				bool flag2 = !flag;
				obj = null;
				if (!flag2)
				{
					BotOwner owner = _owner;
					bool flag3 = enumerator._current.ContainsPlayer(owner.<GetPlayer>k__BackingField);
					bool flag4 = !flag3;
					obj = null;
					if (!flag4)
					{
						break;
					}
				}
			}
			reference = ref *(AIPlaceInfo*)enumerator._current;
			object obj3 = 0;
			enumerator2.Dispose();
			return true;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	private void SetState(EBossWedgeDist next)
	{
		if (<WedgeDist>k__BackingField != next)
		{
			<WedgeDist>k__BackingField = next;
		}
	}

	public override void BossLogicUpdate()
	{
		_periodCheckVision.Update();
		_checkDist.Update();
	}

	public override void Dispose()
	{
		UnsubscribeStopAmbushPlaces();
		if (!((Object)(object)_owner == (Object)null))
		{
			BotOwner owner = _owner;
			if (owner.Memory != null)
			{
				Action<BotOwner> action = null;
				((BossWedge)(object)action).OnGoalEnemyChanged((BotOwner)(object)this);
				owner.Memory.OnGoalEnemyChanged -= action;
			}
			BotOwner owner2 = _owner;
			if (owner2.<BotsGroup>k__BackingField != null)
			{
				Action<BotOwner> action2 = null;
				((BossWedge)(object)action2).OnMemberRemove((BotOwner)(object)this);
				owner2.<BotsGroup>k__BackingField.OnMemberRemove -= action2;
			}
		}
	}

	public AIPlaceInfo FindClosesPlaceExecpt(AIPlaceInfo enemyZone)
	{
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_03da: Unknown result type (might be due to invalid IL or missing references)
		//IL_03df: Unknown result type (might be due to invalid IL or missing references)
		//IL_0330: Unknown result type (might be due to invalid IL or missing references)
		//IL_0335: Unknown result type (might be due to invalid IL or missing references)
		//IL_0498: Unknown result type (might be due to invalid IL or missing references)
		//IL_049c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0699: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_07c0: Expected O, but got Unknown
		//IL_07f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_095f: Expected O, but got I4
		//IL_0a5e: Expected O, but got I4
		//IL_0a78: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a7c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a80: Unknown result type (might be due to invalid IL or missing references)
		//IL_097d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0982: Unknown result type (might be due to invalid IL or missing references)
		//IL_098a: Unknown result type (might be due to invalid IL or missing references)
		//IL_098f: Unknown result type (might be due to invalid IL or missing references)
		//IL_09ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_09b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_09b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_09ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_09f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_09fc: Expected O, but got F4
		//IL_0a04: Expected O, but got F4
		//IL_081c: Unknown result type (might be due to invalid IL or missing references)
		//IL_084b: Unknown result type (might be due to invalid IL or missing references)
		//IL_08d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_08da: Unknown result type (might be due to invalid IL or missing references)
		//IL_08de: Unknown result type (might be due to invalid IL or missing references)
		//IL_08e6: Expected O, but got Unknown
		//IL_0be4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0be9: Expected F4, but got Unknown
		//IL_0916: Unknown result type (might be due to invalid IL or missing references)
		//IL_0af3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0af8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b00: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b05: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b0d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b49: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b4d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b55: Expected O, but got Unknown
		if (_allWedgeZones != null)
		{
			List<AIPlaceInfo> allWedgeZones = _allWedgeZones;
			if (allWedgeZones._size != 0L)
			{
				if (_owner != null)
				{
					Vector3 position = _owner.Position;
					BotOwner owner = _owner;
					BotMemory memory = owner.Memory;
					List<object>.Enumerator enumerator3 = default(List<object>.Enumerator);
					float num8 = default(float);
					float num9 = default(float);
					Object val22 = default(Object);
					if (memory._goalEnemy != null)
					{
						position = memory._goalEnemy.CurrPosition;
						NavMeshPath val = null;
						val..ctor();
						if (_allWedgeZones != null)
						{
							List<AIPlaceInfo>.Enumerator enumerator = _allWedgeZones.GetEnumerator();
							List<AIPlaceInfo>.Enumerator enumerator2 = enumerator;
							NavMeshPath val3 = default(NavMeshPath);
							NavMeshPath val2 = val3;
							Object val5 = default(Object);
							Object val4 = val5;
							Object val6 = null;
							float num = float.MaxValue;
							float num2 = float.MaxValue;
							float z = position.z;
							float z2 = position.z;
							Vector3 val11 = default(Vector3);
							Vector3 val12 = default(Vector3);
							Vector3 point = default(Vector3);
							Bounds bounds = default(Bounds);
							Vector3 a = default(Vector3);
							Vector3 b = default(Vector3);
							Vector3 ownerPos = default(Vector3);
							Vector3 zonePos = default(Vector3);
							Vector3 enemyPos = default(Vector3);
							while (true)
							{
								NavMeshPath val7 = val2;
								Object val8 = val4;
								AIPlaceInfo result = (AIPlaceInfo)(object)val6;
								float num3 = z;
								float num4 = float.MaxValue;
								float num5 = z2;
								float num15;
								Object val16;
								float num56;
								while (true)
								{
									if (!enumerator3.MoveNext())
									{
										enumerator2.Dispose();
										return result;
									}
									if ((Object)(object)enumerator._current == (Object)null || (Object)(object)enumerator._current == (Object)(object)enemyZone)
									{
										continue;
									}
									Vector3 zoneCenter = GetZoneCenter(enumerator._current);
									float num6 = zoneCenter.z - num3;
									float num7 = num8 - num9;
									float num10 = zoneCenter.x - position.x;
									float num11 = num6 * num6;
									float num12 = num7 * num7;
									float num13 = num10 * num10;
									float num14 = num13 + num12;
									num15 = num14 + num11;
									bool flag = val == null;
									NavMeshPath val9 = val7;
									Object val10 = val8;
									bool flag11;
									float x;
									float minDistToEnemySqr;
									NavMeshPath val15;
									Bounds val17;
									Object val18;
									Object val19;
									if (!flag)
									{
										bool flag2 = NavMesh.CalculatePath(val11, val12, -1, val);
										bool flag3 = !flag2;
										val9 = val;
										val10 = null;
										if (!flag3)
										{
											Vector3[] corners = val.corners;
											bool flag4 = corners == null;
											val9 = val;
											val10 = null;
											if (!flag4)
											{
												Vector3[] corners2 = val.corners;
												bool flag5 = corners2.Length < 2;
												val9 = val;
												val10 = null;
												if (!flag5)
												{
													Vector3[] corners3 = val.corners;
													float num16 = MinDistanceSqrToPolylineXZ(point, corners3);
													bool flag6 = (Object)(object)enemyZone != (Object)null;
													bool flag7 = !flag6;
													Bounds val13 = (Bounds)0;
													Object val14 = (Object)(object)enemyZone;
													if (!flag7)
													{
														bool flag8 = (Object)(object)enemyZone.Collider != (Object)null;
														bool flag9 = !flag8;
														val13 = (Bounds)0;
														val14 = (Object)(object)enemyZone.Collider;
														if (!flag9)
														{
															Vector3[] corners4 = val.corners;
															if (enemyZone.Collider != null)
															{
																Vector3 center = ((Collider)enemyZone.Collider).bounds.m_Center;
																bool flag10 = PolylineIntersectsBoundsXZ(corners4, bounds);
																val15 = val;
																val16 = null;
																flag11 = flag10;
																x = zoneCenter.x;
																minDistToEnemySqr = num16;
																val17 = (Bounds)0;
																val18 = (Object)center;
																val19 = (Object)(object)corners4;
																goto IL_0865;
															}
															throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
														}
													}
													val15 = val;
													val16 = null;
													flag11 = false;
													x = zoneCenter.x;
													minDistToEnemySqr = num16;
													val17 = val13;
													val18 = (Object)val13;
													val19 = val14;
													goto IL_0865;
												}
											}
										}
									}
									float num17 = num5 - num3;
									float num18 = position.x - position.x;
									float num19 = zoneCenter.z - num3;
									float num20 = zoneCenter.x - position.x;
									float num21 = num19 * num19;
									float num22 = num20 * num20;
									float num23 = num22 + num21;
									float num38;
									if (float.MaxValue == num23)
									{
										float num24 = num19 * num17;
										float num25 = num20 * num18;
										float num26 = num25 + num24;
										float num27 = num26 / num23;
										float num28;
										if (0f == num27)
										{
											bool flag12 = num27 == num4;
											num28 = num27;
											if (!flag12)
											{
												num28 = num4;
											}
										}
										else
										{
											num28 = 0f;
										}
										float num29 = num19 * num28;
										float num30 = num29 + position.z;
										float num31 = position.z - num30;
										float num32 = num20 * num28;
										float num33 = num32 + position.x;
										float num34 = position.x - num33;
										float num35 = num31 * num31;
										float num36 = num34 * num34;
										float num37 = num36 + num35;
										num38 = num37;
									}
									else
									{
										float num39 = num17 * num17;
										float num40 = num18 * num18;
										float num41 = num40 + num39;
										num38 = num41;
									}
									bool flag13 = (Object)(object)enemyZone != (Object)null;
									bool flag14 = !flag13;
									Bounds val20 = (Bounds)0;
									Object val21 = (Object)(object)enemyZone;
									if (!flag14)
									{
										bool flag15 = (Object)(object)enemyZone.Collider != (Object)null;
										bool flag16 = !flag15;
										val20 = (Bounds)0;
										val21 = (Object)(object)enemyZone.Collider;
										if (!flag16)
										{
											if (enemyZone.Collider != null)
											{
												Vector3 center2 = ((Collider)enemyZone.Collider).bounds.m_Center;
												float x2 = zoneCenter.x;
												float x3 = position.x;
												bool flag17 = SegmentIntersectsBoundsXZ(a, b, bounds);
												val15 = null;
												val16 = val10;
												flag11 = flag17;
												x = zoneCenter.x;
												minDistToEnemySqr = num38;
												val17 = (Bounds)center2;
												val18 = (Object)(object)x2;
												val19 = (Object)(object)x3;
												goto IL_0865;
											}
											throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
										}
									}
									val15 = val9;
									val16 = val10;
									flag11 = false;
									x = zoneCenter.x;
									minDistToEnemySqr = num38;
									val17 = val20;
									val18 = (Object)val20;
									val19 = val21;
									goto IL_0865;
									IL_0865:
									float num43;
									float distToEnemy;
									if (0f == num15)
									{
										float num42 = (float)Math.Sqrt(num15);
										num43 = num42;
										distToEnemy = 0f;
									}
									else
									{
										object obj = 0;
										num43 = num15;
										distToEnemy = num15;
									}
									float num44 = x - position.x;
									float num45 = num44;
									object obj2 = 0;
									float num46 = ComputePassRisk01(minDistToEnemySqr);
									float num47 = ComputeEnemyProximityRisk01(distToEnemy);
									float num48 = ComputeTowardEnemyRisk01(ownerPos, zonePos, enemyPos);
									float num49 = float.MaxValue * num46;
									float num50 = float.MaxValue * num48;
									float num51 = float.MaxValue * num47;
									float num52 = num49 + num43;
									float num53 = num52 + num50;
									float num54 = ((!flag11) ? 0f : float.MaxValue);
									float num55 = num51 + num53;
									num56 = num55 + num54;
									if (num2 != num56)
									{
										break;
									}
									float num57 = num56 - num2;
									float num58 = 0x7F7FFFFFL & num57;
									bool flag18 = float.MaxValue == num58;
									val7 = null;
									val8 = val16;
									result = (AIPlaceInfo)(object)val6;
									num3 = position.z;
									num4 = float.MaxValue;
									num5 = position.z;
									if (!flag18)
									{
										bool flag19 = num == num15;
										val7 = null;
										val8 = val16;
										result = (AIPlaceInfo)(object)val6;
										num3 = position.z;
										num4 = float.MaxValue;
										num5 = position.z;
										if (!flag19)
										{
											break;
										}
									}
								}
								val2 = null;
								val4 = val16;
								val6 = val22;
								num = num15;
								num2 = num56;
								z = position.z;
								z2 = position.z;
							}
						}
					}
					else if ((Object)(object)enemyZone == (Object)null && _allWedgeZones != null)
					{
						List<AIPlaceInfo>.Enumerator enumerator = _allWedgeZones.GetEnumerator();
						List<AIPlaceInfo>.Enumerator enumerator4 = enumerator;
						AIPlaceInfo result2 = null;
						float num59 = float.MaxValue;
						while (enumerator3.MoveNext())
						{
							if (!((Object)(object)enumerator._current == (Object)null))
							{
								position = GetZoneCenter(enumerator._current);
								float num60 = position.x - position.x;
								float num61 = num8 - num9;
								float num62 = position.z - position.z;
								float num63 = num61 * num61;
								float num64 = num60 * num60;
								float num65 = num63 + num64;
								float num66 = num62 * num62;
								float num67 = num65 + num66;
								if (num59 != num67)
								{
									result2 = (AIPlaceInfo)(object)val22;
									num59 = num67;
								}
							}
						}
						enumerator4.Dispose();
						return result2;
					}
				}
				throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
			}
		}
		return null;
	}

	private static Vector3 GetZoneCenter(AIPlaceInfo zone)
	{
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		if (!((Object)(object)zone == (Object)null))
		{
		}
		Vector3 val = (Vector3)0;
		Vector3 result = default(Vector3);
		result.x = val.x;
		result.z = val.z;
		return result;
	}

	private static bool TryCalcNavMeshPath(Vector3 from, Vector3 to, NavMeshPath path)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		Vector3 val = default(Vector3);
		Vector3 val2 = default(Vector3);
		if (path != null && NavMesh.CalculatePath(val, val2, -1, path))
		{
			Vector3[] corners = path.corners;
			if (corners != null)
			{
				Vector3[] corners2 = path.corners;
				int num = corners2.Length - 2;
				int num2 = corners2.Length ^ 2;
				int num3 = corners2.Length ^ num;
				int num4 = num2 & num3;
				bool flag = num4 < 0;
				bool flag2 = num < 0;
				return flag2 == flag;
			}
		}
		return false;
	}

	private static float ComputePassRisk01(float minDistToEnemySqr)
	{
		return 0f;
	}

	private static float ComputeEnemyProximityRisk01(float distToEnemy)
	{
		return 0f;
	}

	private static float ComputeTowardEnemyRisk01(Vector3 ownerPos, Vector3 zonePos, Vector3 enemyPos)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v60 @ rax_v3+8]:4");
	}

	private static float MinDistanceSqrToPolylineXZ(Vector3 point, Vector3[] corners)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v133 @ rcx_v4 (UnityEngine.Vector3[])-C]:8");
	}

	private static float DistancePointToSegmentSqrXZ(Vector3 p, Vector3 a, Vector3 b)
	{
		float num = p.x - a.x;
		float num2 = p.z - a.z;
		float num3 = b.x - a.x;
		float num4 = b.z - a.z;
		float num5 = num3 * num3;
		float num6 = num4 * num4;
		float num7 = num6 + num5;
		if (0.0001f == num7)
		{
			float num8 = num * num3;
			float num9 = num2 * num4;
			float num10 = num8 + num9;
			float num11 = num10 / num7;
			float num12;
			if (0f == num11)
			{
				bool flag = 0.0001f == num11;
				num12 = num11;
				if (!flag)
				{
					num12 = 0.0001f;
				}
			}
			else
			{
				num12 = 0f;
			}
			float num13 = num12 * num3;
			float num14 = num12 * num4;
			float num15 = num13 + a.x;
			float num16 = num14 + a.z;
			float num17 = p.x - num15;
			float num18 = p.z - num16;
			float num19 = num18 * num18;
			float num20 = num17 * num17;
			return num19 + num20;
		}
		float num21 = num2 * num2;
		float num22 = num * num;
		return num21 + num22;
	}

	private static bool PolylineIntersectsBoundsXZ(Vector3[] corners, Bounds bounds)
	{
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Expected O, but got Unknown
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Expected O, but got Unknown
		if (corners != null && corners.Length >= 2)
		{
			object obj = null;
			Vector3 a = default(Vector3);
			Vector3 b = default(Vector3);
			Bounds bounds2 = default(Bounds);
			while (true)
			{
				int num = corners.Length - 1;
				if ((nint)obj >= num)
				{
					break;
				}
				if ((nint)obj < corners.Length)
				{
					object obj2 = obj + 1L;
					if ((nint)obj2 < corners.Length)
					{
						if (!SegmentIntersectsBoundsXZ(a, b, bounds2))
						{
							object obj3 = obj + 1;
							obj = obj3;
							continue;
						}
						return true;
					}
				}
				throw new Exception("Native no-return helper 0x1801FD370 was not resolved");
			}
		}
		return false;
	}

	private static bool SegmentIntersectsBoundsXZ(Vector3 a, Vector3 b, Bounds bounds)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		float x = default(float);
		b.x = x;
		a.x = x;
		object obj = null;
		Vector3 val = default(Vector3);
		float num = ((Bounds)(ref bounds)).SqrDistance(val);
		return true;
	}

	public bool WannaChangeLook(bool isShootFromCover)
	{
		BotOwner owner = _owner;
		bool isInCover = owner.Memory.IsInCover;
		if (isShootFromCover != isInCover)
		{
			BotOwner owner2 = _owner;
			BotMemory memory = owner2.Memory;
			BotCurrentCoverInfo botCurrentCoverInfo = memory.BotCurrentCoverInfo;
			if ((long)botCurrentCoverInfo._shootCoverStatus != 0L)
			{
				return false;
			}
		}
		float time = Time.time;
		bool flag = time < _nextChangeLook;
		bool flag2 = time == _nextChangeLook;
		int num = ((!flag) ? 1 : 0);
		int num2 = ((!flag2) ? 1 : 0);
		return (byte)(num & num2) != 0;
	}

	public void ChangeHold(float nextHold, float nextLook)
	{
		bool flag = (byte)((<PeriodHold>k__BackingField ? 1u : 0u) - 0u) != 0;
		bool flag2 = !flag;
		<PeriodHold>k__BackingField = flag2;
		float nextChangeLook;
		if (<PeriodHold>k__BackingField)
		{
			float time = Time.time;
			float num = time + nextLook;
			nextChangeLook = num;
		}
		else
		{
			float time2 = Time.time;
			float num2 = time2 + nextHold;
			nextChangeLook = num2;
		}
		_nextChangeLook = nextChangeLook;
	}

	private bool <.ctor>b__27_0(AIPlaceInfo x)
	{
		BotOwner owner = _owner;
		AICorePoint <StartCorePoint>k__BackingField = owner.<StartCorePoint>k__BackingField;
		int num = x.ConnectionGroupId - <StartCorePoint>k__BackingField._connectionGroupId;
		return num == 0;
	}
}
