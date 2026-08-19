using System;
using EFT;

public class WedgeFightRequest : FightRequestLayer
{
	protected BossFinder<BossWedge> _boss;

	public WedgeFightRequest(BotOwner bot, int priority)
		: base(bot, priority)
	{
		BossFinder<BossWedge> bossFinder = null;
		bossFinder..ctor(bot);
		_boss = bossFinder;
		_boss.FindBoss();
	}

	public override bool ShallUseNow()
	{
		throw new Exception("Decompilation failed: Unresolved unmanaged memory load: [v36._goalEnemy (EnemyInfo)]:8");
	}

	public override string Name()
	{
		return "wedgeFR";
	}
}
