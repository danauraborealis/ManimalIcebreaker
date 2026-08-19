using System;
using EFT;
using UnityEngine;

public class WedgeGrenadeLayer : BaseWedgeLayer
{
	private PeriodicCheck _distCheck;

	private const float DIST_REACTION_LAYER = 15f;

	public WedgeGrenadeLayer(BotOwner bot, int priority)
		: base(bot, priority)
	{
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Expected O, but got Unknown
		Func<bool> func = null;
		((WedgeGrenadeLayer)(object)func).IsGoodDist();
		PeriodicCheck periodicCheck = null;
		periodicCheck..ctor(func, 3f);
		WedgeGrenadeLayer wedgeGrenadeLayer = (WedgeGrenadeLayer)(this + 168L);
		_distCheck = periodicCheck;
		bool flag = wedgeGrenadeLayer.IsGoodDist();
	}

	private bool IsGoodDist()
	{
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		if (memory._goalEnemy != null)
		{
			float time = Time.time;
			float num = time - goalEnemy.<PersonalLastSeenTime>k__BackingField;
			if (20f == num)
			{
				bool flag = 15f < goalEnemy._distance;
				bool flag2 = 15f == goalEnemy._distance;
				int num2 = ((!flag) ? 1 : 0);
				int num3 = ((!flag2) ? 1 : 0);
				return (byte)(num2 & num3) != 0;
			}
		}
		return false;
	}

	public unsafe override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		//IL_009f: Expected O, but got I
		//IL_00ba: Expected O, but got I8
		//IL_00e1: Expected O, but got I
		//IL_00fc: Expected O, but got I8
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		object obj = goalEnemy;
		object obj2 = default(object);
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> result = default(AICoreActionResult<BotLogicDecision, CoreActionResultParams>);
		object obj5;
		object obj6;
		object obj7;
		object obj8;
		if (obj2 != null)
		{
			object obj3 = goalEnemy;
			object obj4 = default(object);
			if (obj4 != null)
			{
				result.Action = BotLogicDecision.doorOpen;
				result.Data = null;
				obj5 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
				obj6 = "sfat4";
				obj7 = null;
				obj8 = 8L;
				goto IL_0101;
			}
		}
		result.Action = BotLogicDecision.doorOpen;
		result.Data = null;
		obj5 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
		obj6 = "sb4";
		obj7 = null;
		obj8 = 27L;
		goto IL_0101;
		IL_0101:
		return result;
	}

	protected override AICoreActionEnd EndRunToEnemy()
	{
		AICoreActionEnd result = default(AICoreActionEnd);
		result.Value = EndRunToEnemy().Value;
		return result;
	}

	public override bool ShallUseNow()
	{
		BotOwner owner = _owner;
		if (owner.Memory.HaveEnemy)
		{
			BotOwner owner2 = _owner;
			if (owner2.<BewareGrenade>k__BackingField.ShallRunAway())
			{
				return _distCheck.Check();
			}
		}
		return false;
	}

	public override string Name()
	{
		return "WedgeGr";
	}
}
