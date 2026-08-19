using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

public class WedgeCloseDist : BaseWedgeLayer
{
	protected BotMeleeAssaultData _assaultData;

	private AIPeriodAction _provocationPeriod;

	private bool _holdOrDF;

	private float _endDogFight;

	private const float DF_DIST = 5f;

	private List<EPhraseTrigger> _tgriggers;

	public WedgeCloseDist(BotOwner bot, int priority)
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v97 @ r9_v5+20]:8");
	}

	private void Provocation()
	{
		EPhraseTrigger ePhraseTrigger = MyExtensions.RandomElement(_tgriggers);
		BotOwner owner = _owner;
		BotTalk <BotTalk>k__BackingField = owner.<BotTalk>k__BackingField;
		object obj = <BotTalk>k__BackingField;
	}

	public unsafe override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
	{
		//IL_0147: Expected O, but got I
		//IL_0289: Expected O, but got I
		//IL_022a: Expected O, but got I
		//IL_01d5: Expected O, but got I
		//IL_0184: Expected O, but got I
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		object obj = goalEnemy;
		object obj2 = default(object);
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult = default(AICoreActionResult<BotLogicDecision, CoreActionResultParams>);
		object obj5;
		object obj6;
		object obj7;
		BotLogicDecision botLogicDecision;
		AICoreActionResult<BotLogicDecision, CoreActionResultParams> aICoreActionResult2;
		if (obj2 != null)
		{
			object obj3 = goalEnemy;
			object obj4 = default(object);
			if (obj4 != null)
			{
				if (5f == goalEnemy._distance)
				{
					aICoreActionResult.Action = BotLogicDecision.doorOpen;
					aICoreActionResult.Data = null;
					obj5 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
					obj6 = "now!";
					obj7 = null;
					botLogicDecision = BotLogicDecision.shootFromPlace;
					aICoreActionResult2 = aICoreActionResult;
				}
				else
				{
					_endDogFight = -1f;
					aICoreActionResult.Action = BotLogicDecision.doorOpen;
					aICoreActionResult.Data = null;
					obj5 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
					obj6 = "b452!";
					obj7 = null;
					botLogicDecision = BotLogicDecision.dogFight;
					aICoreActionResult2 = aICoreActionResult;
				}
				goto IL_02d2;
			}
		}
		object obj8;
		object obj9;
		object obj10;
		BotLogicDecision botLogicDecision3;
		if (!_assaultData.WantMeleeAssault())
		{
			bool flag = (byte)((_holdOrDF ? 1u : 0u) - 0u) != 0;
			bool holdOrDF = !flag;
			_holdOrDF = holdOrDF;
			if (_holdOrDF)
			{
				BotLogicDecision botLogicDecision2 = HoldFor(float.Epsilon);
				aICoreActionResult.Action = BotLogicDecision.doorOpen;
				aICoreActionResult.Data = null;
				obj8 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
				obj9 = "now!";
				obj10 = null;
				botLogicDecision3 = BotLogicDecision.holdPosition;
			}
			else
			{
				float time = Time.time;
				float endDogFight = 3f + time;
				_endDogFight = endDogFight;
				aICoreActionResult.Action = BotLogicDecision.doorOpen;
				aICoreActionResult.Data = null;
				obj8 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
				obj9 = "dfrnd";
				obj10 = null;
				botLogicDecision3 = BotLogicDecision.dogFight;
			}
		}
		else
		{
			BotLogicDecision botLogicDecision4 = _assaultData.DoSimpleAssault();
			aICoreActionResult.Action = BotLogicDecision.doorOpen;
			aICoreActionResult.Data = null;
			obj8 = (object)__ldftn(AICoreActionResult<BotLogicDecision, CoreActionResultParams>..ctor);
			obj9 = "InFightMele";
			obj10 = null;
			botLogicDecision3 = botLogicDecision4;
		}
		obj5 = obj8;
		obj6 = obj9;
		obj7 = obj10;
		botLogicDecision = botLogicDecision3;
		aICoreActionResult2 = aICoreActionResult;
		goto IL_02d2;
		IL_02d2:
		return aICoreActionResult;
	}

	protected override AICoreActionEnd EndRunToEnemy()
	{
		//IL_008c: Expected I4, but got O
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		object obj = goalEnemy;
		object obj2 = default(object);
		AICoreActionEnd result = default(AICoreActionEnd);
		if (obj2 != null)
		{
			object obj3 = goalEnemy;
			object obj4 = default(object);
			if (obj4 != null)
			{
				result.Value = false;
				return new AICoreActionEnd("ev56");
			}
		}
		result.Value = (byte)(int)ContinueNodeLogic != 0;
		return result;
	}

	protected override AICoreActionEnd EndGoToEnemy()
	{
		//IL_008c: Expected I4, but got O
		BotOwner owner = _owner;
		BotMemory memory = owner.Memory;
		EnemyInfo goalEnemy = memory._goalEnemy;
		object obj = goalEnemy;
		object obj2 = default(object);
		AICoreActionEnd result = default(AICoreActionEnd);
		if (obj2 != null)
		{
			object obj3 = goalEnemy;
			object obj4 = default(object);
			if (obj4 != null)
			{
				result.Value = false;
				return new AICoreActionEnd("ev56");
			}
		}
		result.Value = (byte)(int)ContinueNodeLogic != 0;
		return result;
	}

	protected override AICoreActionEnd EndDogFight()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v43._goalEnemy (EnemyInfo)]:8");
	}

	protected override AICoreActionEnd EndHoldPosition()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v45._goalEnemy (EnemyInfo)]:8");
	}

	public override bool ShallUseNow()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v52 @ rax_v8 (BossFinder`1<BossWedge>)+28]:8");
	}

	public override string Name()
	{
		return "WedgeClose";
	}
}
