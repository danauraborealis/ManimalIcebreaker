using System;
using EFT;
using UnityEngine;

public class WedgeMidDist : BaseWedgeLayer
{
	private float _possibleSuppress;

	private float _startSuppress;

	public WedgeMidDist(BotOwner bot, int priority)
		: base(bot, priority)
	{
	}

	private bool WannaSupress()
	{
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		float time = Time.time;
		float num = time - goalEnemy.<PersonalLastSeenTime>k__BackingField;
		if (10f != num)
		{
			float time2 = Time.time;
			bool flag = time2 < _possibleSuppress;
			bool flag2 = time2 == _possibleSuppress;
			int num2 = ((!flag) ? 1 : 0);
			int num3 = ((!flag2) ? 1 : 0);
			return (byte)(num2 & num3) != 0;
		}
		return false;
	}

	public unsafe override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		//IL_0013: Expected O, but got I4
		//IL_0083: Expected O, but got I
		//IL_0091: Expected O, but got I4
		//IL_009e: Expected O, but got I8
		//IL_02a9: Expected O, but got I
		//IL_02c4: Expected O, but got I8
		//IL_021d: Expected O, but got I
		//IL_0238: Expected O, but got I8
		//IL_01eb: Expected O, but got I
		//IL_0206: Expected O, but got I8
		//IL_03b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c0: Expected O, but got Unknown
		//IL_0368: Expected O, but got I
		//IL_0382: Expected O, but got I8
		//IL_0585: Expected O, but got I4
		//IL_0463: Unknown result type (might be due to invalid IL or missing references)
		//IL_0468: Unknown result type (might be due to invalid IL or missing references)
		//IL_05cf: Expected O, but got I
		//IL_05ea: Expected O, but got I8
		//IL_0533: Unknown result type (might be due to invalid IL or missing references)
		//IL_0564: Expected O, but got F4
		//IL_0569->IL0410: Incompatible stack heights: 1 vs 0
		BotOwner owner = _owner;
		float? cause = (float?)(object)0;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult = default(AICoreActionResult<BotLogicDecision, CoreActionResultParams>);
		object obj;
		object obj2;
		float? num;
		object obj3;
		float? num3;
		object obj8;
		object obj9;
		float? num2;
		object obj10;
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult2;
		if (ShallShootFromCover(out *(string*)(&cause)))
		{
			aICoreActionResult.Action = BotLogicDecision.doorOpen;
			aICoreActionResult.Data = null;
			obj = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
			obj2 = null;
			num = (float?)(object)0;
			obj3 = 17L;
		}
		else
		{
			object obj4 = goalEnemy;
			object obj5 = default(object);
			if (obj5 != null)
			{
				object obj6 = goalEnemy;
				object obj7 = default(object);
				if (obj7 != null)
				{
					bool flag = 5f == goalEnemy._distance;
					aICoreActionResult.Action = BotLogicDecision.doorOpen;
					aICoreActionResult.Data = null;
					if (flag)
					{
						obj8 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
						obj9 = null;
						num2 = (float?)"now!";
						obj10 = 8L;
						aICoreActionResult2 = aICoreActionResult;
					}
					else
					{
						obj8 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
						obj9 = null;
						num2 = (float?)"b452!";
						obj10 = 18L;
						aICoreActionResult2 = aICoreActionResult;
					}
					goto IL_01dc;
				}
			}
			if (!WannaSupress())
			{
				BotOwner owner2 = _owner;
				if (owner2.Memory.IsInCover && !SawEnemyLongTime())
				{
					BotLogicDecision botLogicDecision = HoldFor(0f);
					aICoreActionResult.Action = BotLogicDecision.doorOpen;
					aICoreActionResult.Data = null;
					num3 = (float?)"w8enem";
					goto IL_035e;
				}
				BotOwner owner3 = _owner;
				if (owner3.Memory.IsInCover)
				{
					float time = Time.time;
					BotOwner owner4 = _owner;
					BotMemory memory2 = owner4.Memory;
					float num4 = time - memory2.<ComeToCoverTime>k__BackingField;
					if (5f != num4)
					{
						BotLogicDecision botLogicDecision2 = HoldFor(0f);
						aICoreActionResult.Action = BotLogicDecision.doorOpen;
						aICoreActionResult.Data = null;
						num3 = (float?)"fghdf5";
						goto IL_035e;
					}
				}
				float time2 = Time.time;
				bool flag2 = time2 == base._nextCheckCoverInMiddle;
				Vector3 val = default(Vector3);
				AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult3 = (AICoreActionResult<BotLogicDecision, CoreActionResultParams>)val;
				if (!flag2)
				{
					float time3 = Time.time;
					float nextCheckCoverInMiddle = 5f + time3;
					BotOwner owner5 = _owner;
					base._nextCheckCoverInMiddle = nextCheckCoverInMiddle;
					val = _owner.Position;
					Func<GroupPoint, bool> func = null;
					((BaseWedgeLayer)(object)func).GoodPoint((GroupPoint)(object)this);
					Vector3 pos = default(Vector3);
					int maxIterations = default(int);
					CustomNavigationPoint closestPoint = owner5.<Covers>k__BackingField.GetClosestPoint(pos, func, printErrorLogsIfFail: false, maxIterations);
					_coverInMiddle = closestPoint;
					aICoreActionResult3 = (AICoreActionResult<BotLogicDecision, CoreActionResultParams>)val.x;
				}
				if (_coverInMiddle == null)
				{
					float nextHold = MyExtensions.Random(5f, 5f);
					float nextLook = MyExtensions.Random(5f, 5f);
					aICoreActionResult3 = LookOrHold("lohM", nextHold, nextLook);
					aICoreActionResult.Action = aICoreActionResult3.Action;
					aICoreActionResult.Data = aICoreActionResult3.Data;
					goto IL_01dc;
				}
				BotOwner owner6 = _owner;
				Vector3? val2 = default(Vector3?);
				owner6.Memory.Spotted(byHit: false, val2, (float?)(object)0);
				BotOwner owner7 = _owner;
				owner7.Memory.SetCoverPoints(_coverInMiddle);
				aICoreActionResult.Action = BotLogicDecision.doorOpen;
				aICoreActionResult.Data = null;
				obj = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
				obj2 = null;
				num = (float?)"doFlank";
				obj3 = 65L;
			}
			else
			{
				float time4 = Time.time;
				float possibleSuppress = 20f + time4;
				_possibleSuppress = possibleSuppress;
				float time5 = Time.time;
				BotOwner owner8 = _owner;
				_startSuppress = time5;
				bool flag3 = owner8.<SuppressShoot>k__BackingField.Init(goalEnemy);
				aICoreActionResult.Action = BotLogicDecision.doorOpen;
				aICoreActionResult.Data = null;
				obj = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
				obj2 = null;
				num = (float?)"spr";
				obj3 = 20L;
			}
		}
		goto IL_00c8;
		IL_01dc:
		return aICoreActionResult;
		IL_00c8:
		obj8 = obj;
		obj9 = obj2;
		num2 = num;
		obj10 = obj3;
		aICoreActionResult2 = aICoreActionResult;
		goto IL_01dc;
		IL_035e:
		obj = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
		obj2 = null;
		num = num3;
		obj3 = 3L;
		goto IL_00c8;
	}

	protected override AICoreActionEnd EndGoToEnemy()
	{
		//IL_000f: Expected I4, but got O
		AICoreActionEnd result = default(AICoreActionEnd);
		result.Value = (byte)(int)FinishNodeLogic != 0;
		return result;
	}

	protected override AICoreActionEnd EndHoldPosition()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v48 @ rax_v11 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndSuppressFromCover()
	{
		//IL_004f: Expected I4, but got O
		float time = Time.time;
		float num = time - _startSuppress;
		AICoreActionEnd result = default(AICoreActionEnd);
		if (10f == num)
		{
			result.Value = (byte)(int)ContinueNodeLogic != 0;
			return result;
		}
		result.Value = false;
		return new AICoreActionEnd("tio");
	}

	private bool SawEnemyLongTime()
	{
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		float time = Time.time;
		float num = time - goalEnemy.<PersonalLastSeenTime>k__BackingField;
		bool flag = 10f < num;
		bool flag2 = 10f == num;
		int num2 = ((!flag) ? 1 : 0);
		int num3 = ((!flag2) ? 1 : 0);
		return (byte)(num2 & num3) != 0;
	}

	protected override AICoreActionEnd EndSuppressFire()
	{
		AICoreActionEnd result = default(AICoreActionEnd);
		result.Value = EndSuppressFire().Value;
		return result;
	}

	public override CustomNavigationPoint FindPoint(CoverSearchData data, Func<CoverSearchData, CustomNavigationPoint> p, bool checkCurrent)
	{
		if (_coverInMiddle != null)
		{
			BotOwner owner = _owner;
			if (_coverInMiddle.IsFreeById(owner.<Id>k__BackingField))
			{
				return _coverInMiddle;
			}
		}
		return FindPoint(data, p, checkCurrent);
	}

	protected override AICoreActionEnd EndShootFromCover()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v28 @ rax_v6 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndAttackMovingFlank()
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
		return new AICoreActionEnd("mgjh7");
	}

	public override bool ShallUseNow()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v52 @ rax_v8 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override string Name()
	{
		return "WedgeMid";
	}
}
