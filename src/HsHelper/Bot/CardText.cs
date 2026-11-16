using System.Linq;
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
		private static readonly Regex HealWord = new Regex(@"\bheal(ed|s)?\b", O);
		private static readonly Regex Restore = new Regex(@"\brestore\s+\d+", O);
		private static readonly Regex Trigger = new Regex(
			@"^(whenever|after|at the (end|start)|when |for the rest|secret|spellburst|inspire|overkill|frenzy|deathrattle|while|each turn|if you|your next|has \+|can't|costs?)", O);
		// 目標名詞前面的字：這些才是「出牌時選目標」（對…、給予、消滅、沉默、冰凍、變形、返回、選擇、設定…）
		private static readonly Regex TargetVerb = new Regex(
			@"\b(to|give|destroy|silence|freeze|transform|return|choose|set|steal|swap|heal|restore|change|adapt|copy of|control of|on|(attack|health) of)\s+$", O);

		/// <summary>句子裡有「一個 X」，而且前面是選目標的動詞（排除「抽一張手下」「使用一個手下後」「揭示一個手下」等）。</summary>
		private static bool IsTargetPhrase(string clause)
		{
			foreach(Match m in Targeted.Matches(clause))
			{
				var before = clause.Substring(0, m.Index);
				var after = clause.Substring(m.Index + m.Length);
				// 「你牌庫／手牌裡的手下」不是場上的目標
				if(System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s+(in|from) (your|your opponent's|the) (deck|hand)", RegexOptions.IgnoreCase))
					continue;
				if(TargetVerb.IsMatch(before))
					return true;
			}
			return false;
		}
		private static readonly Regex BuffRe = new Regex(@"\+\d+/\+\d+|\+\d+ (attack|health)", O);

		// 敘述中出現「選一個 X」的寫法 → 需要目標
		private static readonly Regex Targeted = new Regex(
			@"\b(a|an|another) (damaged |undamaged |friendly |enemy |other |frozen )*(minion|character|enemy|beast|demon|dragon|mech|murloc|pirate|elemental|undead|naga|totem)\b", O);

		// 加水晶：Innervate / Counterfeit Coin「Gain 1 Mana Crystal this turn only」、Horn of Winter「Refresh 2 Mana Crystals」
		private static readonly Regex ManaGainRe = new Regex(@"\b(gain|refresh) (\d+|a|an) mana crystals?( this turn only)?", O);

		public static void Classify(Ent e, bool isHeroPower = false)
		{
			var t = e.Text = Regex.Replace(Clean.Replace((e.Text ?? "").Replace("[x]", " "), "").Replace("_", " "), @"\s+", " ").Trim();
			var dm = DealDamage.Match(t);
			e.Damage = dm.Success ? int.Parse(dm.Groups[1].Value) : 0;
			var mg = ManaGainRe.Match(t);
			if(mg.Success && e.Kind == Kind.Spell)
			{
				var v = mg.Groups[2].Value.ToLowerInvariant();
				e.ManaGain = v == "a" || v == "an" ? 1 : int.Parse(v);
				e.ManaRefresh = mg.Groups[1].Value.ToLowerInvariant() == "refresh";
			}
			e.AoE = AllMinions.IsMatch(t) && (dm.Success || t.IndexOf("destroy", System.StringComparison.OrdinalIgnoreCase) >= 0);

			// 手下只有「戰吼」那段會在出牌時選目標；沒有戰吼的手下不需要目標
			var scope = t;
			if(e.Kind == Kind.Minion)
			{
				var bi = t.IndexOf("battlecry", System.StringComparison.OrdinalIgnoreCase);
				if(bi < 0)
				{
					e.Target = TargetKind.None;
					return;
				}
				scope = t.Substring(bi + "battlecry".Length).TrimStart(':', ' ');
			}

			// 依句子拆開：目標由「出現目標名詞的那一句」決定（例：靈魂虹吸「消滅一個手下。替你的英雄恢復 3 點生命。」→ 目標是敵方手下）
			// 觸發句（每當／之後／回合開始或結束時／奧秘…）是之後才發生的效果，出牌時不用選目標
			var clauses = scope.Split(new[] { '.', ';' }, System.StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim())
				.Where(c => c.Length > 0 && !Trigger.IsMatch(c)).ToList();
			bool Plain(string c) => !Random.IsMatch(c) && !AllMinions.IsMatch(c) && !AllEnemy.IsMatch(c)
			                        && c.IndexOf("enemy hero", System.StringComparison.OrdinalIgnoreCase) < 0
			                        && c.IndexOf("your hero", System.StringComparison.OrdinalIgnoreCase) < 0
			                        && c.IndexOf("all ", System.StringComparison.OrdinalIgnoreCase) < 0;

			// 「a random minion」本身不會被當成目標名詞，所以句子後半有 random（變成隨機一個…）不影響
			var clause = clauses.FirstOrDefault(c => IsTargetPhrase(c) && !AllMinions.IsMatch(c));
			// 沒寫目標名詞的「造成 X 點傷害」（火球術、法師英雄技能、精靈弓箭手）
			clause ??= clauses.FirstOrDefault(c => DealDamage.IsMatch(c) && Plain(c));
			// 沒寫目標名詞的「恢復 X 點生命」（巫毒巫醫、牧師英雄技能）
			clause ??= clauses.FirstOrDefault(c => Restore.IsMatch(c) && Plain(c));

			if(clause == null)
			{
				e.Target = TargetKind.None;
				return;
			}
			var lower = clause.ToLowerInvariant();
			bool Has(string x) => lower.Contains(x);

			if(Has("friendly") || Has("your minion") || Has("give") || BuffRe.IsMatch(lower))
				e.Target = Has("character") ? TargetKind.FriendlyAny : TargetKind.FriendlyMinion;
			else if(Has("restore") || HealWord.IsMatch(lower))
				e.Target = TargetKind.FriendlyAny;
			else if(DealDamage.IsMatch(lower) || (Has("deal") && Has("damage")))
				e.Target = Has("minion") ? TargetKind.EnemyMinion : TargetKind.EnemyAny; // 含傷害值不固定的「造成等同於…的傷害」
			else if(Has("freeze") && !Has("minion"))
				e.Target = TargetKind.EnemyAny; // 「冰凍一個敵人」可以選英雄
			else if(Has("destroy") || Has("transform") || Has("silence") || Has("freeze") || Has("return") || Has("enemy") || Has("set ") || Has("change") || Has("health to 1"))
				e.Target = TargetKind.EnemyMinion;
			else
				e.Target = TargetKind.Unknown;
		}
	}
}
