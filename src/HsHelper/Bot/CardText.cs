using System.Collections.Generic;
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

		// 棄牌：末日守衛「Discard two random cards」、靈魂之火「Discard a random card」；排除「每當你棄掉…」這種觸發
		private static readonly Regex DiscardRe = new Regex(@"(?<!you |whenever |when |if you )\bdiscard (your hand|(a|an|one|two|three|\d+)( random| your (lowest|highest)-cost)? cards?)", O);

		// ───────── v0.9.5：結構化效果 ─────────
		private const string Num = @"(a|an|one|two|three|four|five|six|seven|\d+)";
		private static readonly Regex SummonRe = new Regex(@"\bsummon " + Num + @" (\d+)/(\d+)", O);
		private static readonly Regex DrawRe = new Regex(@"(?<!opponent |they |each player |both players )\bdraw " + Num + @" (cards?|minions?|spells?)", O);
		private static readonly Regex ArmorRe = new Regex(@"\bgain (\d+) armor", O);
		private static readonly Regex BuffAllRe = new Regex(@"\bgive (your|all friendly|your other|all your) minions \+(\d+)/\+(\d+)", O);
		private static readonly Regex BuffAllAtkRe = new Regex(@"\bgive (your|all friendly|your other|all your) minions \+(\d+) attack", O);
		private static readonly Regex FaceRe = new Regex(@"\bdeal (\d+) damage to (the enemy hero|your opponent)", O);
		private static readonly Regex DiscoverRe = new Regex(@"\bdiscover\b", O);
		private static readonly Regex AddHandRe = new Regex(@"\b(add|get) " + Num + @" .{0,60}?(to your hand|\bcards?\b|\bspells?\b|\bminions?\b|copies)", O);
		private static readonly Regex HeroAtkRe = new Regex(@"\bgive your hero \+(\d+) attack", O);
		private static readonly Regex RandomCostRe = new Regex(@"\bsummon " + Num + @" random (\d+)-cost", O);
		private static readonly Regex HealRe = new Regex(@"\brestore (\d+) health", O);

		private static int N(string v)
		{
			switch(v.ToLowerInvariant())
			{
				case "a": case "an": case "one": return 1;
				case "two": return 2; case "three": return 3; case "four": return 4;
				case "five": return 5; case "six": return 6; case "seven": return 7;
				default: return int.TryParse(v, out var n) ? n : 1;
			}
		}

		/// <summary>出牌當下的效果文字（手下只看戰吼；去掉觸發句）。</summary>
		public static string PlayScope(Ent e)
		{
			var t = e.Text ?? "";
			if(e.Kind == Kind.Minion)
			{
				var bi = t.IndexOf("battlecry", System.StringComparison.OrdinalIgnoreCase);
				if(bi < 0) return "";
				t = t.Substring(bi + 9).TrimStart(':', ' ');
			}
			var clauses = t.Split(new[] { '.', ';' }, System.StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim())
				.Where(c => c.Length > 0 && !Trigger.IsMatch(c));
			return string.Join(". ", clauses);
		}

		private static void Extras(Ent e)
		{
			var t = PlayScope(e);
			if(t.Length == 0) return;
			var m = SummonRe.Match(t);
			if(m.Success) { e.SumN = N(m.Groups[1].Value); e.SumA = int.Parse(m.Groups[2].Value); e.SumH = int.Parse(m.Groups[3].Value); }
			m = DrawRe.Match(t);
			if(m.Success) e.Draw = N(m.Groups[1].Value);
			m = ArmorRe.Match(t);
			if(m.Success) e.GainArmor = int.Parse(m.Groups[1].Value);
			m = BuffAllRe.Match(t);
			if(m.Success) { e.BuffAllA = int.Parse(m.Groups[2].Value); e.BuffAllH = int.Parse(m.Groups[3].Value); }
			else if((m = BuffAllAtkRe.Match(t)).Success) e.BuffAllA = int.Parse(m.Groups[2].Value);
			m = FaceRe.Match(t);
			if(m.Success) e.FaceDmg = int.Parse(m.Groups[1].Value);
			m = HealRe.Match(t);
			if(m.Success) e.Heal = int.Parse(m.Groups[1].Value);
			if(DiscoverRe.IsMatch(t)) e.Gen = 1;
			else if((m = AddHandRe.Match(t)).Success && !t.ToLowerInvariant().Contains("opponent's hand")) e.Gen = N(m.Groups[2].Value);
			m = HeroAtkRe.Match(t);
			if(m.Success) e.HeroAtk = int.Parse(m.Groups[1].Value);
			if(e.SumN == 0 && (m = RandomCostRe.Match(t)).Success)
			{
				var cost = int.Parse(m.Groups[2].Value);
				e.SumN = N(m.Groups[1].Value); e.SumA = cost; e.SumH = cost + 1; // 隨機 N 費手下：平均約 N/N+1
			}
		}

		/// <summary>「選一個手下。之後…」：目標由後面的句子決定（v0.9.5 全卡掃描）。</summary>
		private static TargetKind ChooseRule(string clause, string scope)
		{
			var c = clause.ToLowerInvariant();
			if(!System.Text.RegularExpressions.Regex.IsMatch(c, @"^choose an? (minion|character|enemy minion|friendly minion)$"))
				return TargetKind.Unknown;
			var rest = scope.ToLowerInvariant();
			if(c.Contains("enemy")) return TargetKind.EnemyMinion;
			if(c.Contains("friendly")) return TargetKind.FriendlyMinion;
			if(System.Text.RegularExpressions.Regex.IsMatch(rest, @"deal \d+ damage") && c.Contains("character")) return TargetKind.EnemyAny;
			if(System.Text.RegularExpressions.Regex.IsMatch(rest, @"destroy it|can't attack|dormant|steals?|freeze it|silence it|attack it|deal \d+ damage to it|it takes"))
				return TargetKind.EnemyMinion;
			if(System.Text.RegularExpressions.Regex.IsMatch(rest, @"\bgive it|\+\d+|whenever it attacks|reborn|divine shield|spell damage|restore|return it to the battlefield|has stealth"))
				return TargetKind.FriendlyMinion;
			return TargetKind.Unknown;
		}

		/// <summary>這張牌的能力標籤（面板與 log 顯示用；全卡掃描也用它統計辨識率）。</summary>
		public static List<string> Tags(Ent e)
		{
			var t = new List<string>();
			if(e.IsCoin || e.ManaGain > 0) t.Add(e.ManaRefresh ? $"重置{e.ManaGain}水晶" : $"+{(e.IsCoin ? 1 : e.ManaGain)}水晶");
			if(e.AoE) t.Add(e.Damage > 0 ? $"全場{e.Damage}傷" : "全場消滅");
			else if(e.Damage > 0 && e.Target != TargetKind.None) t.Add($"{e.Damage}傷害");
			if(e.FaceDmg > 0) t.Add($"打臉{e.FaceDmg}");
			var low = (PlayScope(e) ?? "").ToLowerInvariant();
			if(!e.AoE && e.Damage == 0 && (e.Target == TargetKind.EnemyMinion || e.Target == TargetKind.EnemyAny))
				t.Add(low.Contains("destroy") ? "消滅" : low.Contains("transform") ? "變形" : low.Contains("silence") ? "沉默"
					: low.Contains("freeze") ? "冰凍" : low.Contains("return") ? "彈回" : "對敵");
			if(e.Target == TargetKind.FriendlyMinion || e.Target == TargetKind.FriendlyAny)
				t.Add(e.Heal > 0 ? $"治療{e.Heal}" : "增益友方");
			else if(e.Heal > 0) t.Add($"治療{e.Heal}");
			if(e.BuffAllA > 0 || e.BuffAllH > 0) t.Add($"全體+{e.BuffAllA}/+{e.BuffAllH}");
			if(e.SumN > 0) t.Add($"召喚{e.SumN}×{e.SumA}/{e.SumH}");
			if(e.Draw > 0) t.Add($"抽{e.Draw}");
			if(e.Gen > 0) t.Add(DiscoverRe.IsMatch(PlayScope(e)) ? "發現" : $"拿{e.Gen}張牌");
			if(e.HeroAtk > 0) t.Add($"英雄+{e.HeroAtk}攻");
			if(e.GainArmor > 0) t.Add($"護甲{e.GainArmor}");
			if(e.Discard > 0) t.Add($"棄{(e.Discard >= 10 ? "全部" : e.Discard.ToString())}");
			if(e.Kind == Kind.Minion)
			{
				if(e.Taunt) t.Add("嘲諷");
				if(e.Charge) t.Add("衝鋒"); else if(e.Rush) t.Add("突襲");
				if(e.DivineShield) t.Add("聖盾");
				if(e.Lifesteal) t.Add("吸血");
				if(e.Poisonous) t.Add("劇毒");
				if(e.Windfury) t.Add("風怒");
			}
			if(e.Target == TargetKind.Unknown) t.Add("目標看不懂");
			return t;
		}

		public static void Classify(Ent e, bool isHeroPower = false)
		{
			ClassifyTarget(e, isHeroPower);
			Extras(e);
		}

		private static void ClassifyTarget(Ent e, bool isHeroPower)
		{
			var t = e.Text = Regex.Replace(Clean.Replace((e.Text ?? "").Replace("[x]", " "), "").Replace("_", " "), @"\s+", " ").Trim();
			var dm = DealDamage.Match(t);
			e.Damage = dm.Success ? int.Parse(dm.Groups[1].Value) : 0;
			var dc = DiscardRe.Match(t);
			if(dc.Success)
			{
				var v = dc.Groups[2].Value.ToLowerInvariant();
				e.Discard = dc.Groups[1].Value.ToLowerInvariant() == "your hand" ? 10
					: v == "a" || v == "an" || v == "one" ? 1 : v == "two" ? 2 : v == "three" ? 3 : int.TryParse(v, out var dn) ? dn : 1;
			}
			var mg = ManaGainRe.Match(t);
			if(mg.Success && e.Kind == Kind.Spell)
			{
				var v = mg.Groups[2].Value.ToLowerInvariant();
				e.ManaGain = v == "a" || v == "an" ? 1 : int.Parse(v);
				e.ManaRefresh = mg.Groups[1].Value.ToLowerInvariant() == "refresh";
			}
			e.AoE = (AllMinions.IsMatch(t) || AllEnemy.IsMatch(t) || System.Text.RegularExpressions.Regex.IsMatch(t, @"\ball enemies\b", RegexOptions.IgnoreCase)) && (dm.Success || t.IndexOf("destroy", System.StringComparison.OrdinalIgnoreCase) >= 0);

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
				e.Target = ChooseRule(clause, scope);
		}
	}
}
