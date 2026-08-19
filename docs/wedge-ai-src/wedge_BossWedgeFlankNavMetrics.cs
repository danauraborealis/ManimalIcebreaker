using System;
using EFT;
using UnityEngine;
using UnityEngine.AI;

public sealed class BossWedgeFlankNavMetrics
{
	public const float COOLDOWN = 10f;

	public const float COOLDOWN_GOTO_ENEMY = 10f;

	private const float PATH_CACHE_PERIOD = 5f;

	private readonly NavMeshPath _navPathBuffer;

	private float _pathToEnemyCacheTime;

	private float _pathToEnemyCacheLength;

	private int _pathToEnemyCacheBotId;

	private int _pathToEnemyCacheGoalEnemyRaidId;

	private float _lastFlankToPointStartTime;

	private float _lastGoToEnemyStartTime;

	private BotOwner _lastFlankToPointStarter;

	private BotOwner _lastGoToEnemyStarter;

	public void CopyFrom(BossWedgeFlankNavMetrics other)
	{
		if (other != null)
		{
			_pathToEnemyCacheTime = other._pathToEnemyCacheTime;
			_pathToEnemyCacheLength = other._pathToEnemyCacheLength;
			_pathToEnemyCacheBotId = other._pathToEnemyCacheBotId;
			_pathToEnemyCacheGoalEnemyRaidId = other._pathToEnemyCacheGoalEnemyRaidId;
			_lastFlankToPointStartTime = other._lastFlankToPointStartTime;
			_lastGoToEnemyStartTime = other._lastGoToEnemyStartTime;
			_lastFlankToPointStarter = other._lastFlankToPointStarter;
			_lastGoToEnemyStarter = other._lastGoToEnemyStarter;
		}
	}

	public void OnGroupMemberRemoved(BotOwner removedMember)
	{
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Expected O, but got Unknown
		//IL_00a8: Expected O, but got I4
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Expected O, but got Unknown
		//IL_00f4: Expected O, but got I4
		if (!((Object)(object)removedMember == (Object)null))
		{
			if (removedMember.<Id>k__BackingField == _pathToEnemyCacheBotId)
			{
				_pathToEnemyCacheBotId = -1;
			}
			if ((Object)(object)removedMember == (Object)(object)_lastFlankToPointStarter)
			{
				BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics = (BossWedgeFlankNavMetrics)(this + 48L);
				_lastFlankToPointStarter = null;
				object obj = 0;
			}
			if ((Object)(object)removedMember == (Object)(object)_lastGoToEnemyStarter)
			{
				BossWedgeFlankNavMetrics bossWedgeFlankNavMetrics2 = (BossWedgeFlankNavMetrics)(this + 56L);
				_lastGoToEnemyStarter = null;
				object obj2 = 0;
			}
		}
	}

	public void RegisterAttackMovingFlankToPointStarted(BotOwner starter)
	{
		float time = Time.time;
		_lastFlankToPointStartTime = time;
		_lastFlankToPointStarter = starter;
	}

	public void RegisterGoToEnemyStarted(BotOwner starter)
	{
		float time = Time.time;
		_lastGoToEnemyStartTime = time;
		_lastGoToEnemyStarter = starter;
	}

	public unsafe bool ShallDeferFlankBecauseRecentGroupFlank(BotOwner requestingBot, out BotLogicDecision substituteDecision)
	{
		ref BotLogicDecision reference = ref *(BotLogicDecision*)9L;
		if (!((Object)(object)_lastFlankToPointStarter == (Object)null))
		{
			float time = Time.time;
		}
		return false;
	}

	public bool ShallHoldBecauseRecentGroupGoToEnemy(BotOwner requestingBot)
	{
		if (!((Object)(object)_lastGoToEnemyStarter == (Object)null))
		{
			float time = Time.time;
		}
		return false;
	}

	public static float GetRemainingDistanceAlongActivePath(BotOwner bot)
	{
		if (bot == null || bot.<Mover>k__BackingField == null || !bot.<Mover>k__BackingField.HasPathAndNoComplete)
		{
			return 0f;
		}
		return bot.<Mover>k__BackingField.DistDestination;
	}

	public float GetPathLengthToGoalEnemy(BotOwner bot)
	{
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0174: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c8: Invalid comparison between Unknown and I4
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f1: Expected O, but got I4
		int num;
		float num4;
		if (bot != null)
		{
			BotMemory memory = bot.Memory;
			if (bot.Memory != null && memory._goalEnemy != null)
			{
				EnemyInfo goalEnemy = memory._goalEnemy;
				if (goalEnemy.<Person>k__BackingField != null)
				{
					num = 0;
					if (_pathToEnemyCacheBotId == bot.<Id>k__BackingField && _pathToEnemyCacheGoalEnemyRaidId == num)
					{
						float time = Time.time;
						float num2 = time - _pathToEnemyCacheTime;
						if (5f != num2)
						{
							return _pathToEnemyCacheLength;
						}
					}
					BotMemory memory2 = bot.Memory;
					if (memory2._goalEnemy != null)
					{
						Vector3 currPosition = memory2._goalEnemy.CurrPosition;
						Vector3 position = bot.Position;
						float x = currPosition.x;
						Vector3 val = default(Vector3);
						Vector3 val2 = default(Vector3);
						bool flag = NavMesh.CalculatePath(val, val2, -1, _navPathBuffer);
						NavMeshPathStatus status = _navPathBuffer.status;
						if ((int)status == 0)
						{
							Vector3[] corners = _navPathBuffer.corners;
							if (corners != null)
							{
								Vector3[] corners2 = _navPathBuffer.corners;
								if (corners2.Length >= 2)
								{
									Vector3[] corners3 = _navPathBuffer.corners;
									float num3 = NavMeshPathExtension.CalculatePathLength(corners3);
									num4 = num3;
									goto IL_022b;
								}
							}
						}
						position = bot.Position;
						object obj = 0;
						num4 = currPosition.z;
						goto IL_022b;
					}
				}
				throw new Exception("Native no-return helper 0x1802361B0 was not resolved");
			}
		}
		return 0f;
		IL_022b:
		float time2 = Time.time;
		_pathToEnemyCacheTime = time2;
		_pathToEnemyCacheLength = num4;
		_pathToEnemyCacheBotId = bot.<Id>k__BackingField;
		_pathToEnemyCacheGoalEnemyRaidId = num;
		return num4;
	}

	public BossWedgeFlankNavMetrics()
	{
		NavMeshPath val = null;
		val..ctor();
		_navPathBuffer = val;
		_pathToEnemyCacheTime = float.NegativeInfinity;
		_pathToEnemyCacheBotId = -1;
		_lastFlankToPointStartTime = float.NegativeInfinity;
		_lastGoToEnemyStartTime = float.NegativeInfinity;
	}
}
