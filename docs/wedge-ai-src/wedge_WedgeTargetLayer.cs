using System;
using EFT;
using UnityEngine;

public class WedgeTargetLayer : BaseWedgeLayer
{
	private float _nextCoverChnage;

	private const float SDIST_FAR_OK = 289f;

	public WedgeTargetLayer(BotOwner bot, int priority)
		: base(bot, priority)
	{
	}

	public override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		//IL_00cc: Expected O, but got I4
		//IL_00d5: Expected O, but got I4
		float time = Time.time;
		bool flag = time == _nextCoverChnage;
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult2 = default(AICoreActionResult<BotLogicDecision, CoreActionResultParams>);
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult = aICoreActionResult2;
		if (!flag)
		{
			float time2 = Time.time;
			float num = MyExtensions.Random(15f, 15f);
			BotOwner owner = _owner;
			float nextCoverChnage = num + time2;
			_nextCoverChnage = nextCoverChnage;
			Vector3? val = default(Vector3?);
			owner.Memory.Spotted(byHit: false, val, (float?)(object)0);
			aICoreActionResult = (AICoreActionResult<BotLogicDecision, CoreActionResultParams>)0;
		}
		BotOwner owner2 = _owner;
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> result = default(AICoreActionResult<BotLogicDecision, CoreActionResultParams>);
		if (!owner2.Memory.IsInCover)
		{
			result.Action = BotLogicDecision.doorOpen;
			result.Data = null;
			return new AICoreActionResult<BotLogicDecision, CoreActionResultParams>(BotLogicDecision.attackMoving, "rn");
		}
		float nextHold = MyExtensions.Random(15f, 15f);
		float nextLook = MyExtensions.Random(15f, 15f);
		aICoreActionResult = LookOrHold("lohT", nextHold, nextLook);
		result.Action = aICoreActionResult.Action;
		result.Data = aICoreActionResult.Data;
		return result;
	}

	protected override AICoreActionEnd EndShootFromCover()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v48 @ rax_v9 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndHoldPosition()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v48 @ rax_v9 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override CustomNavigationPoint FindPoint(CoverSearchData data, Func<CoverSearchData, CustomNavigationPoint> p, bool checkCurrent)
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		data.ArrayType = PointsArrayType.covers;
		if (_owner != null)
		{
			Vector3 position = _owner.Position;
			float min = default(float);
			Vector3 val = MyExtensions.RandomHorizontal(min, 5f);
			float z = position.z + val.z;
			Vector3 centerPos = default(Vector3);
			data.CenterPos = centerPos;
			data.CenterPos.z = z;
			BotOwner owner = _owner;
			Func<GroupPoint, bool> func = null;
			((WedgeTargetLayer)(object)func).IsFarEnought((GroupPoint)(object)this);
			Vector3 pos = default(Vector3);
			int maxIterations = default(int);
			CustomNavigationPoint closestPoint = owner.<Covers>k__BackingField.GetClosestPoint(pos, func, printErrorLogsIfFail: false, maxIterations);
			bool flag = closestPoint != null;
			CustomNavigationPoint result = closestPoint;
			if (!flag)
			{
				CustomNavigationPoint customNavigationPoint = FindPoint(data, p, checkCurrent);
				result = customNavigationPoint;
			}
			return result;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	private bool IsFarEnought(GroupPoint arg)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Invalid comparison between I4 and Unknown
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Invalid comparison between I4 and Unknown
		if (_owner != null)
		{
			Vector3 position = _owner.Position;
			Vector3 val = (Vector3)(arg._position - position.x);
			Vector3 val3 = default(Vector3);
			Vector3 val4 = default(Vector3);
			Vector3 val2 = (Vector3)(val3 - val4);
			float num = arg._position.z - position.z;
			Vector3 val5 = (Vector3)(val2 * val2);
			Vector3 val6 = (Vector3)(val * val);
			float num2 = num * num;
			Vector3 val7 = (Vector3)(val5 + val6);
			Vector3 val8 = (Vector3)(val7 + num2);
			bool flag = 1133543424 < (int)val8;
			bool flag2 = 1133543424 == (int)val8;
			int num3 = ((!flag) ? 1 : 0);
			int num4 = ((!flag2) ? 1 : 0);
			return (byte)(num3 & num4) != 0;
		}
		throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
	}

	public override bool ShallUseNow()
	{
		BotOwner owner = _owner;
		return owner.Memory.HaveGoal;
	}

	public override string Name()
	{
		return "WdgTarget";
	}
}
