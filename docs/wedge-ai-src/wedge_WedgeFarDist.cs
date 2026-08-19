using System;
using EFT;
using UnityEngine;

public class WedgeFarDist : BaseWedgeLayer
{
	private AIPeriodAction _usingStims;

	private new CustomNavigationPoint _coverInMiddle;

	private float _nextCheckCoverInMiddle;

	public unsafe WedgeFarDist(BotOwner bot, int priority)
		: base(bot, priority)
	{
		Action action = null;
		action..ctor(this, (nint)__ldftn(WedgeFarDist.TryUsingStims));
		AIPeriodAction aIPeriodAction = null;
		aIPeriodAction..ctor(120f, action);
		_usingStims = aIPeriodAction;
	}

	private void TryUsingStims()
	{
		//IL_0069: Expected O, but got I4
		BotOwner owner = _owner;
		BotMedecine <Medecine>k__BackingField = owner.<Medecine>k__BackingField;
		<Medecine>k__BackingField.Stimulators.Refresh();
		BotOwner owner2 = _owner;
		BotMedecine <Medecine>k__BackingField2 = owner2.<Medecine>k__BackingField;
		<Medecine>k__BackingField2.Stimulators.TryApply(noCheckDelay: false, (int?)(object)0);
	}

	public override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		throw new Exception("Decompilation failed: Unknown operand: &v193 @ stack_-48 (AICoreActionResult`2<BotLogicDecision, CoreActionResultParams>)");
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

	protected override AICoreActionEnd EndHoldPosition()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v74 @ rax_v13 (BossFinder`1<BossWedge>)+28]:8");
	}

	protected override AICoreActionEnd EndShootFromCover()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v44 @ rax_v7 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override bool ShallUseNow()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v52 @ rax_v8 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override string Name()
	{
		return "WedgeFar";
	}
}
