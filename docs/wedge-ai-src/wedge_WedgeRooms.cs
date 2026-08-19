using System;
using EFT;
using UnityEngine;

public class WedgeRooms(BotOwner bot, int priority) : BaseWedgeLayer(bot, priority)
{
	private const float STIM_COOLDOWN_SEC = 30f;

	private const float STIM_NEED_NO_PERSONAL_SIGHT_SEC = 15f;

	private new CustomNavigationPoint _coverInMiddle;

	private float _nextRefreshCover;

	private float _lastChangeCover;

	public const float STEP_AWAY_DIST = 5f;

	private bool _endAttacklMoving;

	private int _curPlaceId = -1;

	private float _nextCanGoRound;

	public override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v963 @ rax_v32+8]:4");
	}

	protected override AICoreActionEnd EndRunToEnemy()
	{
		AICoreActionEnd aICoreActionEnd = default(AICoreActionEnd);
		aICoreActionEnd.Value = false;
		return new AICoreActionEnd("noRun");
	}

	private bool CanGoRound()
	{
		BotOwner owner = _owner;
		BotMoveByMiddleGraph <MoveByMiddleGraph>k__BackingField = owner.<MoveByMiddleGraph>k__BackingField;
		if (<MoveByMiddleGraph>k__BackingField.<TotalyBlocked>k__BackingField)
		{
			return false;
		}
		float time = Time.time;
		bool flag = time < _nextCanGoRound;
		bool flag2 = time == _nextCanGoRound;
		int num = ((!flag) ? 1 : 0);
		int num2 = ((!flag2) ? 1 : 0);
		return (byte)(num & num2) != 0;
	}

	protected override AICoreActionEnd EndRunToCover()
	{
		//IL_000f: Expected I4, but got O
		AICoreActionEnd result = default(AICoreActionEnd);
		result.Value = (byte)(int)FinishNodeLogic != 0;
		return result;
	}

	private AICoreActionResult<BotLogicDecision, CoreActionResultParams> AttackMovingFlankToPointWithGroupCooldown(string flankReason)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v65 @ rax_v9 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndAttackMovingFlankToPoint()
	{
		//IL_0063: Expected I4, but got O
		float time = Time.time;
		float num = time - ((AICoreLayer<BotLogicDecision>)this).<LastChangeDecision>k__BackingField;
		AICoreActionEnd result = default(AICoreActionEnd);
		if (0.5f == num)
		{
			BotOwner owner = _owner;
			if (!owner.<Mover>k__BackingField.HasPathAndNoComplete)
			{
				float time2 = Time.time;
				float nextCanGoRound = 0.5f + time2;
				_nextCanGoRound = nextCanGoRound;
				result.Value = false;
				return new AICoreActionEnd("nPath");
			}
			BotOwner owner2 = _owner;
			BotMoveByMiddleGraph <MoveByMiddleGraph>k__BackingField = owner2.<MoveByMiddleGraph>k__BackingField;
			if (!<MoveByMiddleGraph>k__BackingField._trackingGoRoundMoveActive)
			{
				float time3 = Time.time;
				float nextCanGoRound2 = 0.5f + time3;
				_nextCanGoRound = nextCanGoRound2;
				result.Value = false;
				return new AICoreActionEnd("ntm");
			}
		}
		result.Value = (byte)(int)ContinueNodeLogic != 0;
		return result;
	}

	private void DelayNextGoRound()
	{
		float time = Time.time;
		float nextCanGoRound = 20f + time;
		_nextCanGoRound = nextCanGoRound;
	}

	private bool WannaHelp()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v40 @ rax_v6 (BossFinder`1<BossWedge>)+28]:8");
	}

	private bool ShouldDoSuppress()
	{
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		if (memory._goalEnemy != null)
		{
			float time = Time.time;
			float num = time - goalEnemy.<PersonalLastSeenTime>k__BackingField;
			bool flag = 20f < num;
			bool flag2 = 20f == num;
			int num2 = ((!flag) ? 1 : 0);
			int num3 = ((!flag2) ? 1 : 0);
			return (byte)(num2 & num3) != 0;
		}
		return false;
	}

	private bool ShallTryStimulatorsInRooms(EnemyInfo enemy)
	{
		BotOwner owner = _owner;
		BotMedecine <Medecine>k__BackingField = owner.<Medecine>k__BackingField;
		BotFirstAid firstAid = <Medecine>k__BackingField.FirstAid;
		if (firstAid.<Damaged>k__BackingField)
		{
			BotOwner owner2 = _owner;
			BotMedecine <Medecine>k__BackingField2 = owner2.<Medecine>k__BackingField;
			if (!<Medecine>k__BackingField2.<Using>k__BackingField)
			{
				float time = Time.time;
				if (enemy == null)
				{
					throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
				}
			}
		}
		return false;
	}

	protected override AICoreActionEnd EndGoToEnemy()
	{
		//IL_000f: Expected I4, but got O
		AICoreActionEnd result = default(AICoreActionEnd);
		result.Value = (byte)(int)FinishNodeLogic != 0;
		return result;
	}

	public override CustomNavigationPoint FindPoint(CoverSearchData data, Func<CoverSearchData, CustomNavigationPoint> p, bool checkCurrent)
	{
		TryRefreshCover();
		if (_coverInMiddle == null || _coverInMiddle.IsSpotted)
		{
			return FindPoint(data, p, checkCurrent);
		}
		return _coverInMiddle;
	}

	private bool SawEnemyLongTime()
	{
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		float time = Time.time;
		float num = time - goalEnemy.<PersonalLastSeenTime>k__BackingField;
		bool flag = 15f < num;
		bool flag2 = 15f == num;
		int num2 = ((!flag) ? 1 : 0);
		int num3 = ((!flag2) ? 1 : 0);
		return (byte)(num2 & num3) != 0;
	}

	private bool GetHitRecently()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v17._goalEnemy (EnemyInfo)]:8");
	}

	protected override AICoreActionEnd EndHoldPosition()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v46._goalEnemy (EnemyInfo)]:8");
	}

	protected override AICoreActionEnd EndAttackMoving()
	{
		//IL_004c: Expected I4, but got O
		BotOwner owner = _owner;
		AICoreActionEnd result = default(AICoreActionEnd);
		if (!owner.Memory.IsInCover)
		{
			result.Value = (byte)(int)ContinueNodeLogic != 0;
			return result;
		}
		_endAttacklMoving = true;
		result.Value = false;
		return new AICoreActionEnd("inCvr");
	}

	private void TryRefreshCover()
	{
		//IL_01a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Expected O, but got I4
		//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_020b: Expected F4, but got O
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Expected O, but got I4
		//IL_0268: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		//IL_0278: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153->IL0120: Incompatible stack heights: 1 vs 0
		//IL_021e->IL0036: Incompatible stack heights: 1 vs 0
		//IL_025f->IL0120: Incompatible stack heights: 1 vs 0
		float time = Time.time;
		if (time == _nextRefreshCover)
		{
			return;
		}
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		float time2 = Time.time;
		bool flag = _coverInMiddle == null;
		float nextRefreshCover = 10f + time2;
		_nextRefreshCover = nextRefreshCover;
		bool num;
		Vector3 trg;
		Func<GroupPoint, bool> isGood;
		Vector3 pos;
		BotCoversData <Covers>k__BackingField;
		if (flag || _coverInMiddle.IsSpotted)
		{
			CoverSearchData coverSearchData = ShootDataCover();
			BotOwner owner2 = _owner;
			if (coverSearchData != null)
			{
				Func<GroupPoint, bool> func = null;
				num = ((WedgeRooms)(object)func).GoodPickCover((GroupPoint)(object)this);
				if (owner2.<Covers>k__BackingField != null)
				{
					Vector3 val = default(Vector3);
					trg = val;
					isGood = func;
					object obj = default(object);
					pos = (Vector3)obj;
					<Covers>k__BackingField = owner2.<Covers>k__BackingField;
					goto IL_01e4;
				}
			}
		}
		else if (_coverInMiddle != null)
		{
			object obj2 = 0;
			if (memory._goalEnemy != null)
			{
				Vector3 val = memory._goalEnemy.CurrPosition;
				float x = val.x;
				float num3 = default(float);
				float num2 = num3;
				object obj3 = 0;
				if (10f == num3)
				{
					return;
				}
				CoverSearchData coverSearchData2 = ShootDataCover();
				BotOwner owner3 = _owner;
				if (coverSearchData2 != null)
				{
					Func<GroupPoint, bool> func2 = null;
					num = ((WedgeRooms)(object)func2).GoodPickCover((GroupPoint)(object)this);
					if (owner3.<Covers>k__BackingField != null)
					{
						trg = (Vector3)num2;
						isGood = func2;
						pos = val;
						<Covers>k__BackingField = owner3.<Covers>k__BackingField;
						goto IL_01e4;
					}
				}
			}
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
		IL_01e4:
		bool noRestrictions = default(bool);
		bool printErrorLogsIfFail = default(bool);
		CustomNavigationPoint coverInMiddle = <Covers>k__BackingField.FindClosestPoint(pos, (float)typeof(!!0), trg, noRestrictions, isGood, printErrorLogsIfFail);
		_coverInMiddle = coverInMiddle;
	}

	private bool GoodPickCover(GroupPoint arg)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Expected O, but got Unknown
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Expected F4, but got O
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		if (memory._goalEnemy != null)
		{
			EnemyInfo enemyInfo2 = default(EnemyInfo);
			EnemyInfo enemyInfo = (EnemyInfo)(enemyInfo2 + -56L);
			BotGroupEnemyInfo groupInfo = default(BotGroupEnemyInfo);
			enemyInfo2.GroupInfo = groupInfo;
			Vector3 currPosition = enemyInfo.CurrPosition;
			Vector3 posToHide = default(Vector3);
			Vector3 wallVector = default(Vector3);
			bool useAng = default(bool);
			Vector3 carePosition = default(Vector3);
			if (!PointsSearchHelper.CanIHideFromPos(posToHide, wallVector, (float)typeof(!!0), useRaycast: false, useAng, carePosition))
			{
				goto IL_00d5;
			}
		}
		if ((long)arg.CoverLevel == 0L || (long)arg.PointWithNeighborType == 0L)
		{
			return true;
		}
		goto IL_00d5;
		IL_00d5:
		return false;
	}

	protected CoverSearchData ShootDataCover()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v320 @ rax_v14+8]:4");
	}

	public override bool ShallUseNow()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v48 @ rax_v6 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override string Name()
	{
		return "WedgeRooms";
	}
}
