using System;
using EFT;
using UnityEngine;

public abstract class BaseWedgeLayer : BaseLogicLayer
{
	private sealed class <>c__DisplayClass8_0
	{
		public AIPlaceInfo place;

		internal bool <TryChangeSectorOrHold>b__0(GroupPoint point)
		{
			AIPlaceInfo aIPlaceInfo = place;
			int num = point.PlaceId - aIPlaceInfo.AreaId;
			return num == 0;
		}
	}

	protected const float ENEMY_FORGOT = 15f;

	protected CustomNavigationPoint _coverInMiddle;

	private float _nextCheckCoverInMiddle;

	protected BossFinder<BossWedge> _boss;

	private const float NEXT_COVER_DELTA = 7f;

	protected BaseWedgeLayer(BotOwner bot, int priority)
		: base(bot, priority)
	{
		BossFinder<BossWedge> bossFinder = null;
		bossFinder..ctor(bot);
		_boss = bossFinder;
		_boss.FindBoss();
	}

	protected bool GoodPoint(GroupPoint arg)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Expected F4, but got Unknown
		if (_owner != null)
		{
			Vector3 position = _owner.Position;
			float num = position.x - arg._position;
			float num2 = position.z - arg._position.z;
			float num4 = default(float);
			float num3 = position.y - num4;
			float num5 = num3 * num3;
			float num6 = num * num;
			float num7 = num2 * num2;
			float num8 = num5 + num6;
			float num9 = num8 + num7;
			bool flag = 49f < num9;
			bool flag2 = 49f == num9;
			int num10 = ((!flag) ? 1 : 0);
			int num11 = ((!flag2) ? 1 : 0);
			return (byte)(num10 & num11) != 0;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	protected AICoreActionResult<BotLogicDecision, CoreActionResultParams> LookOrHold(string keyWork, float nextHold, float nextLook)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v35 @ rax_v4 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected bool TryChangeSectorOrHold(out AICoreActionResult<BotLogicDecision, CoreActionResultParams> result)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v57 @ r8_v1 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndRunToCover()
	{
		//IL_000f: Expected I4, but got O
		AICoreActionEnd result = default(AICoreActionEnd);
		result.Value = (byte)(int)FinishNodeLogic != 0;
		return result;
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
		result.Value = false;
		return new AICoreActionEnd("inCvr");
	}

	protected void RecheckCoverAtMiddle()
	{
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad->IL0057: Incompatible stack heights: 1 vs 0
		float time = Time.time;
		if (time != _nextCheckCoverInMiddle)
		{
			float time2 = Time.time;
			float nextCheckCoverInMiddle = 1f + time2;
			BotOwner owner = _owner;
			_nextCheckCoverInMiddle = nextCheckCoverInMiddle;
			Vector3 position = _owner.Position;
			Func<GroupPoint, bool> func = null;
			((BaseWedgeLayer)(object)func).GoodPoint((GroupPoint)(object)this);
			Vector3 pos = default(Vector3);
			int maxIterations = default(int);
			CustomNavigationPoint closestPoint = owner.<Covers>k__BackingField.GetClosestPoint(pos, func, printErrorLogsIfFail: false, maxIterations);
			_coverInMiddle = closestPoint;
		}
	}
}
