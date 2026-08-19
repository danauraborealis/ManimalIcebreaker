using System;
using EFT;
using EFT.Ballistics;
using UnityEngine;

public class BossWedgeAmbush : BaseWedgeLayer
{
	private static class <>O
	{
		public static Func<GroupPoint, bool> <0>__IsShootCoverPoint;
	}

	private sealed class <>c__DisplayClass3_0
	{
		public BossWedgeAmbush <>4__this;

		public Func<CoverSearchData, CustomNavigationPoint> standardFind;

		internal CustomNavigationPoint <FindPoint>b__0(CoverSearchData d)
		{
			//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
			//IL_0089: Expected O, but got I
			//IL_009a->IL009a: Incompatible stack heights: 1 vs 0
			BossWedgeAmbush bossWedgeAmbush = <>4__this;
			BotOwner owner = bossWedgeAmbush._owner;
			CustomNavigationPoint result;
			if (d != null)
			{
				bool flag = <>O.<0>__IsShootCoverPoint != null;
				Func<GroupPoint, bool> goodFunc = <>O.<0>__IsShootCoverPoint;
				if (!flag)
				{
					Func<GroupPoint, bool> func = null;
					IsShootCoverPoint((GroupPoint)(object)func);
					<>O.<0>__IsShootCoverPoint = func;
					bool flag2 = IsShootCoverPoint((GroupPoint)0);
					goodFunc = func;
				}
				Vector3 pos = default(Vector3);
				int maxIterations = default(int);
				CustomNavigationPoint closestPoint = owner.<Covers>k__BackingField.GetClosestPoint(pos, goodFunc, printErrorLogsIfFail: false, maxIterations);
				if (closestPoint != null)
				{
					GroupPoint groupPoint = closestPoint._groupPoint;
					if ((long)groupPoint.PointWithNeighborType == 0L || (long)groupPoint.PointWithNeighborType == 2L)
					{
						bool flag3 = groupPoint.CanLookLeft;
						result = closestPoint;
						if (!flag3)
						{
							bool flag4 = groupPoint.CanLookRight;
							result = closestPoint;
							if (!flag4)
							{
								goto IL_00e8;
							}
						}
						goto IL_018d;
					}
				}
				goto IL_00e8;
			}
			throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
			IL_018d:
			return result;
			IL_00e8:
			Func<CoverSearchData, CustomNavigationPoint> func2 = standardFind;
			CustomNavigationPoint customNavigationPoint = func2(d);
			result = customNavigationPoint;
			goto IL_018d;
		}
	}

	private bool _isGetHitted;

	public unsafe BossWedgeAmbush(BotOwner bot, int priority)
		: base(bot, priority)
	{
		BotOwner owner = _owner;
		Action<DamageInfo, EBodyPart, float> action = null;
		action..ctor((object)this, (IntPtr)(nint)__ldftn(BossWedgeAmbush.OnGetHit));
		owner.<GetPlayer>k__BackingField.BeingHitAction += action;
	}

	private void OnGetHit(DamageInfo arg1, EBodyPart arg2, float arg3)
	{
		_isGetHitted = true;
	}

	public override CustomNavigationPoint FindPoint(CoverSearchData data, Func<CoverSearchData, CustomNavigationPoint> standardFind, bool checkCurrent)
	{
		<>c__DisplayClass3_0 <>c__DisplayClass3_1 = null;
		<>c__DisplayClass3_1.<>4__this = this;
		<>c__DisplayClass3_1.standardFind = standardFind;
		Func<CoverSearchData, CustomNavigationPoint> func = null;
		((<>c__DisplayClass3_0)(object)func).<FindPoint>b__0((CoverSearchData)(object)<>c__DisplayClass3_1);
		return StandartFindPoint(data, func, checkCurrent);
	}

	private static bool IsShootCoverPoint(GroupPoint point)
	{
		if ((long)point.PointWithNeighborType == 0L || (long)point.PointWithNeighborType == 2L)
		{
			if (point.CanLookLeft)
			{
				return true;
			}
			return point.CanLookRight;
		}
		return false;
	}

	public override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v44._goalEnemy (EnemyInfo)]:8");
	}

	protected override AICoreActionEnd EndRunToCover()
	{
		AICoreActionEnd aICoreActionEnd = default(AICoreActionEnd);
		aICoreActionEnd.Value = false;
		return new AICoreActionEnd("now");
	}

	protected override AICoreActionEnd EndAttackMoving()
	{
		//IL_00d6: Expected I4, but got O
		BotOwner owner = _owner;
		AICoreActionEnd result = default(AICoreActionEnd);
		bool val;
		string reason;
		if (!owner.Memory.IsInCover)
		{
			BotOwner owner2 = _owner;
			BotWeaponManager <WeaponManager>k__BackingField = owner2.<WeaponManager>k__BackingField;
			if (!<WeaponManager>k__BackingField.<Stationary>k__BackingField.ShallEndShootFromCurrent())
			{
				result.Value = (byte)(int)ContinueNodeLogic != 0;
				return result;
			}
			result.Value = false;
			val = true;
			reason = "stationary";
		}
		else
		{
			result.Value = false;
			val = true;
			reason = "inCvr";
		}
		return new AICoreActionEnd(reason, val);
	}

	protected override AICoreActionEnd EndHoldPosition()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v28 @ rax_v7 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndShootFromCover()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v44 @ rax_v7 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override bool ShallUseNow()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v100 @ rax_v8 (BossFinder`1<BossWedge>)+28]:8");
	}

	public unsafe override void Dispose()
	{
		BotOwner owner = _owner;
		Action<DamageInfo, EBodyPart, float> action = null;
		action..ctor((object)this, (IntPtr)(nint)__ldftn(BossWedgeAmbush.OnGetHit));
		owner.<GetPlayer>k__BackingField.BeingHitAction -= action;
		Dispose();
	}

	public override string Name()
	{
		return "wedgeAmbush";
	}
}
