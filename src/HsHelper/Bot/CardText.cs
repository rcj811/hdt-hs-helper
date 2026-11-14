using System.Text.RegularExpressions;

namespace HsHelper.Bot
{
	/// <summary>用卡牌英文敘述粗略判斷：要不要選目標、選誰、是不是全場效果。</summary>
	public static class CardText
	{
		private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Compiled;

		private static readonly Regex Clean = new Regex(@"<[^>]+>|[\$#\[\]]", O);
		private static readonly Regex DealDamage = new Regex(@"deal (\d+) damage", O);
		private static readonly Regex AllMinions = new Regex(@"\b(all|each) (other |enemy )?(minions|characters)\b", O);
		private static readonly Regex AllEnemy = new Regex(@"\b(all|each) enemy\b|\benemy minions\b", O);
		private static readonly Regex Random = new Regex(@"\brandom\b|\brandomly\b", O);

		// 敘述中出現「選一個 X」的寫法 → 需要目標
		private static readonly Regex Targeted = new Regex(
			@"\b(a|an|another) (damaged |undamaged |friendly |enemy |other |frozen )*(minion|character|beast|demon|dragon|mech|murloc|pirate|elemental|undead|naga|totem)\b", O);

		public static void Classify(Ent e, bool isHeroPower = false)
		{
			var t = e.Text = Clean.Replace(e.Text ?? "", "");
			var dm = DealDamage.Match(t);
			e.Damage = dm.Success ? int.Parse(dm.Groups[1].Value) : 0;
			e.AoE = AllMinions.IsMatch(t) && (dm.Success || t.IndexOf("destroy", System.StringComparison.OrdinalIgnoreCase) >= 0);

			var needsTarget = Targeted.IsMatch(t) && !Random.IsMatch(t) && !AllMinions.IsMatch(t);
			if(!needsTarget && dm.Success && !Random.IsMatch(t) && !AllEnemy.IsMatch(t) && !AllMinions.IsMatch(t)
			   && t.IndexOf("enemy hero", System.StringComparison.OrdinalIgnoreCase) < 0
			   && t.IndexOf("your hero", System.StringComparison.OrdinalIgnoreCase) < 0)
				needsTarget = true; // 例：火球術「造成 6 點傷害」、法師英雄技能「造成 1 點傷害」
			if(isHeroPower && t.IndexOf("restore", System.StringComparison.OrdinalIgnoreCase) >= 0 && !Random.IsMatch(t)
			   && t.IndexOf("your hero", System.StringComparison.OrdinalIgnoreCase) < 0)
				needsTarget = true; // 例：牧師英雄技能「恢復 2 點生命」

			if(!needsTarget)
			{
				e.Target = TargetKind.None;
				return;
			}
			var lower = t.ToLowerInvariant();
			bool Has(string s) => lower.Contains(s);

			if(Has("friendly") || Has("give") || Has("+") || Has("your minion"))
				e.Target = TargetKind.FriendlyMinion;
			else if(Has("restore") || Has("heal"))
				e.Target = TargetKind.FriendlyAny;
			else if(dm.Success)
				e.Target = Has("minion") ? TargetKind.EnemyMinion : TargetKind.EnemyAny;
			else if(Has("destroy") || Has("transform") || Has("silence") || Has("freeze") || Has("return") || Has("enemy"))
				e.Target = TargetKind.EnemyMinion;
			else
				e.Target = TargetKind.Unknown;
		}
	}
}
