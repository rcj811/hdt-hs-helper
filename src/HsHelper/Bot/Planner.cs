using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HsHelper.Bot
{
	public enum ActionType { Play, HeroPower, Attack, EndTurn }

	public class BotAction
	{
		public ActionType Type;
		public Ent Source;
		public Ent Target;        // 可為 null
		public string Describe;
		public override string ToString() => Describe;
	}

	/// <summary>
	/// 規則式決策：每次只回傳「下一個」動作，執行完重新讀狀態再決定下一步。
	/// 順序：致命一擊 → 出牌（法力用最多）→ 英雄技能 → 攻擊（嘲諷優先、划算交換、否則打臉）→ 結束回合。
	/// </summary>
	public static class Planner
	{
		public static BotAction Next(Snapshot s, ISet<int> skip)
		{
			var lethal = LethalAttack(s, skip);
			if(lethal != null)
				return lethal;
			return PlayCard(s, skip)
			       ?? UseHeroPower(s, skip)
			       ?? Attack(s, skip)
			       ?? new BotAction { Type = ActionType.EndTurn, Describe = "結束回合" };
		}

		// ───────── 換牌 ─────────
		/// <summary>
		/// 換牌（曲線原則，參考 Hearthbuddy 社群常用的換牌規則）：
		/// 留 3 費以下；後手（有幸運幣）且手上已有 3 費以下的牌時，可以多留一張 4 費；其餘換掉。
		/// </summary>
		public static bool ShouldReplace(Ent card, IList<Ent> hand, bool hasCoin)
		{
			if(card.IsCoin || card.Cost <= 3)
				return false;
			if(card.Cost == 4 && hasCoin)
			{
				var cheap = hand.Count(c => !c.IsCoin && c.Cost <= 3);
				var firstFour = hand.First(c => !c.IsCoin && c.Cost == 4);
				return !(cheap >= 1 && firstFour.Id == card.Id);
			}
			return true;
		}

		// ───────── 攻擊 ─────────
		private static bool CanAttack(Ent a, out bool minionsOnly)
		{
			minionsOnly = false;
			if(a == null || a.Atk <= 0 || a.Frozen || a.CantAttack || a.Dormant || a.AttacksThisTurn >= a.MaxAttacks)
				return false;
			if(a.Kind == Kind.Minion && a.Rush && !a.Charge && a.TurnsInPlay == 0)
			{
				minionsOnly = true; // 突襲：上場當回合只能打手下（實測突襲手下沒有疲勞標記，要先判斷）
				return true;
			}
			return !a.Exhausted || a.Charge;
		}

		private static IEnumerable<(Ent a, bool minionsOnly)> Attackers(Snapshot s, ISet<int> skip)
		{
			foreach(var m in s.MyBoard.Where(m => m.Kind == Kind.Minion && !skip.Contains(m.Id)))
				if(CanAttack(m, out var mo))
					yield return (m, mo);
			if(s.MyHero != null && !skip.Contains(s.MyHero.Id) && CanAttack(s.MyHero, out var hmo))
				yield return (s.MyHero, hmo);
		}

		private static bool Targetable(Ent e) => !e.Stealth && !e.Immune && !e.Dormant && !e.Untouchable && e.Kind == Kind.Minion;

		private static List<Ent> EnemyTaunts(Snapshot s) => s.OppBoard.Where(e => Targetable(e) && e.Taunt).ToList();

		private static bool Kills(Ent a, Ent d) => !d.DivineShield && (a.Atk >= d.Hp || (a.Poisonous && a.Kind == Kind.Minion));
		private static bool Survives(Ent a, Ent d) =>
			a.Kind == Kind.Hero ? a.Hp + a.Armor > d.Atk + 10 : a.DivineShield || (a.Hp > d.Atk && !d.Poisonous);

		private static BotAction AttackAction(Ent a, Ent t, string why) =>
			new BotAction { Type = ActionType.Attack, Source = a, Target = t, Describe = $"{a.Name} 攻擊 {t.Name}（{why}）" };

		private static BotAction LethalAttack(Snapshot s, ISet<int> skip)
		{
			if(s.OppHero == null || s.OppHero.Immune || EnemyTaunts(s).Count > 0)
				return null;
			var face = Attackers(s, skip).Where(x => !x.minionsOnly).ToList();
			var dmg = face.Sum(x => x.a.Atk * (x.a.MaxAttacks - x.a.AttacksThisTurn));
			if(face.Count == 0 || dmg < s.OppHero.Hp + s.OppHero.Armor)
				return null;
			return AttackAction(face[0].a, s.OppHero, "斬殺");
		}

		private static BotAction Attack(Snapshot s, ISet<int> skip)
		{
			var attackers = Attackers(s, skip).ToList();
			if(attackers.Count == 0)
				return null;
			var minionAttackers = attackers.Where(x => x.a.Kind == Kind.Minion).Select(x => x.a).ToList();
			var hero = attackers.Where(x => x.a.Kind == Kind.Hero).Select(x => x.a).FirstOrDefault();

			// 1. 有嘲諷：一定要先處理
			var taunts = EnemyTaunts(s);
			if(taunts.Count > 0)
			{
				var t = taunts.OrderByDescending(x => x.Value).First();
				var pool = attackers.Select(x => x.a).Where(a => a.Kind == Kind.Minion || Survives(a, t)).ToList();
				if(pool.Count == 0)
					return null;
				if(t.DivineShield) // 先用最弱的去破盾
					return AttackAction(pool.OrderBy(a => a.Atk).First(), t, "破聖盾");
				var killer = pool.Where(a => Kills(a, t)).OrderByDescending(a => Survives(a, t)).ThenBy(a => a.Value).FirstOrDefault();
				return AttackAction(killer ?? pool.OrderByDescending(a => a.Atk).First(), t, "解嘲諷");
			}

			// 2. 划算的交換
			BotAction best = null;
			var bestScore = 0.0;
			foreach(var d in s.OppBoard.Where(Targetable))
			{
				foreach(var a in minionAttackers.Concat(hero != null ? new[] { hero } : new Ent[0]))
				{
					if(a.Kind == Kind.Hero && (d.Atk > 3 || !Kills(a, d)))
						continue;
					var score = 0.0;
					if(Kills(a, d))
					{
						score = d.Value - (Survives(a, d) ? 0 : a.Value * 0.9);
						if(d.Atk >= 4)
							score += 1.5; // 威脅大的手下優先清
					}
					else if(d.DivineShield && a.Atk <= 2 && d.Value >= 4 && Survives(a, d))
						score = 1.0; // 小手下去破盾
					if(score > bestScore)
					{
						bestScore = score;
						best = AttackAction(a, d, Survives(a, d) ? "划算交換" : "等價交換");
					}
				}
			}
			if(best != null)
				return best;

			// 3. 打臉
			if(s.OppHero != null && !s.OppHero.Immune)
			{
				var faceAttacker = attackers.FirstOrDefault(x => !x.minionsOnly).a;
				if(faceAttacker != null)
					return AttackAction(faceAttacker, s.OppHero, "打臉");
			}
			return null;
		}

		// ───────── 出牌 ─────────
		private static bool Playable(Snapshot s, Ent c, ISet<int> skip)
		{
			if(skip.Contains(c.Id))
				return false;
			switch(c.Kind)
			{
				case Kind.Minion:
					return s.MyBoard.Count < 7;
				case Kind.Weapon:
				case Kind.Hero:
					return true;
				case Kind.Spell:
					if(c.IsCoin)
						return true;
					if(c.AoE && !AoEWorthIt(s, c))
						return false;
					return c.Target == TargetKind.None || PickTarget(s, c, c.Target) != null;
				default:
					return false; // 地標等先不處理
			}
		}

		private static bool AoEWorthIt(Snapshot s, Ent c)
		{
			bool Dies(Ent m) => c.Damage > 0 ? !m.DivineShield && m.Hp <= c.Damage : true;
			var enemyLoss = s.OppBoard.Where(Dies).Sum(m => m.Value);
			var hitsOwn = !c.Text.ToLowerInvariant().Contains("enemy");
			var ownLoss = hitsOwn ? s.MyBoard.Where(Dies).Sum(m => m.Value) : 0;
			return enemyLoss >= 4 && enemyLoss > ownLoss * 1.5;
		}

		private static BotAction PlayCard(Snapshot s, ISet<int> skip)
		{
			var candidates = s.Hand.Where(c => !c.IsCoin && Playable(s, c, skip)).ToList();
			var coin = s.Hand.FirstOrDefault(c => c.IsCoin && !skip.Contains(c.Id));

			var plan = BestSet(candidates, s.Mana, 7 - s.MyBoard.Count);
			if(coin != null)
			{
				var withCoin = BestSet(candidates, s.Mana + 1, 7 - s.MyBoard.Count);
				if(withCoin.Sum(c => c.Cost) > plan.Sum(c => c.Cost) && withCoin.Sum(c => c.Cost) == s.Mana + 1)
					return new BotAction { Type = ActionType.Play, Source = coin, Describe = "使用幸運幣" };
			}
			if(plan.Count == 0)
				return null;

			// 出牌順序：武器 → 指定敵方目標的法術 → 手下（高費先）→ 其他法術
			var next = plan
				.OrderBy(c => c.Kind == Kind.Weapon ? 0
					: c.Kind == Kind.Spell && (c.Target == TargetKind.EnemyAny || c.Target == TargetKind.EnemyMinion) ? 1
					: c.Kind == Kind.Minion || c.Kind == Kind.Hero ? 2 : 3)
				.ThenByDescending(c => c.Cost)
				.First();
			var target = next.Target != TargetKind.None ? PickTarget(s, next, next.Target) : null;
			return new BotAction
			{
				Type = ActionType.Play, Source = next, Target = target,
				Describe = target != null ? $"打出 {next.Name} → {target.Name}" : $"打出 {next.Name}"
			};
		}

		/// <summary>在法力限制內挑一組牌：花最多法力，平手時手下多的優先。手牌最多 10 張，直接窮舉。</summary>
		private static List<Ent> BestSet(List<Ent> cards, int mana, int boardSpace)
		{
			var best = new List<Ent>();
			var bestScore = -1.0;
			var n = Math.Min(cards.Count, 10);
			for(var mask = 1; mask < (1 << n); mask++)
			{
				int cost = 0, minions = 0;
				for(var i = 0; i < n; i++)
					if((mask & (1 << i)) != 0)
					{
						cost += cards[i].Cost;
						if(cards[i].Kind == Kind.Minion)
							minions++;
					}
				if(cost > mana || minions > boardSpace)
					continue;
				var score = cost * 10 + minions;
				if(score > bestScore)
				{
					bestScore = score;
					best = Enumerable.Range(0, n).Where(i => (mask & (1 << i)) != 0).Select(i => cards[i]).ToList();
				}
			}
			return best;
		}

		/// <summary>依目標類型挑目標；挑不到回傳 null（代表這張牌現在不出）。</summary>
		public static Ent PickTarget(Snapshot s, Ent card, TargetKind kind)
		{
			var enemies = s.OppBoard.Where(Targetable).ToList();
			var friends = s.MyBoard.Where(m => m.Kind == Kind.Minion).ToList();
			switch(kind)
			{
				case TargetKind.EnemyAny:
				case TargetKind.EnemyMinion:
					if(card.Damage > 0)
					{
						var kill = enemies.Where(e => !e.DivineShield && e.Hp <= card.Damage).OrderByDescending(e => e.Value).FirstOrDefault();
						if(kill != null)
							return kill;
						if(kind == TargetKind.EnemyAny && s.OppHero != null && !s.OppHero.Immune)
							return s.OppHero;
						return enemies.OrderByDescending(e => e.Atk).FirstOrDefault();
					}
					if((card.Text ?? "").IndexOf("freeze", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						// 冰凍：凍住攻擊力最高、還沒被凍的敵方手下；沒有就凍英雄
						var f = enemies.Where(e => !e.Frozen && e.Atk > 0).OrderByDescending(e => e.Atk).FirstOrDefault();
						if(f != null)
							return f;
						return kind == TargetKind.EnemyAny && s.OppHero != null && !s.OppHero.Immune ? s.OppHero : enemies.FirstOrDefault();
					}
					// 消滅／變形等：留給有價值的目標
					return enemies.Where(e => e.Value >= 5).OrderByDescending(e => e.Value).FirstOrDefault();
				case TargetKind.FriendlyMinion:
					// 「消滅／犧牲一個友方手下」挑最弱的；增益挑最強的
					var harmful = Regex.IsMatch(card.Text ?? "", @"\b(destroy|sacrifice|deal|transform|silence|return)\b", RegexOptions.IgnoreCase);
					return harmful ? friends.OrderBy(m => m.Value).FirstOrDefault() : friends.OrderByDescending(m => m.Value).FirstOrDefault();
				case TargetKind.FriendlyAny:
					var hurt = friends.Where(m => m.Hp < 3).OrderByDescending(m => m.Value).FirstOrDefault();
					return s.MyHero != null && s.MyHero.Hp < 25 ? s.MyHero : hurt ?? s.MyHero;
				default:
					return null;
			}
		}

		/// <summary>
		/// 戰吼手下已經放下、遊戲在等選目標時用：一定要選一個合法目標（取消的話整張牌會被退回）。
		/// 先用 PickTarget 的好目標，沒有就挑「傷害最小」的合法目標。
		/// </summary>
		public static Ent PickAnyTarget(Snapshot s, Ent card, TargetKind kind)
		{
			var good = PickTarget(s, card, kind);
			if(good != null)
				return good;
			var enemies = s.OppBoard.Where(Targetable).ToList();
			var friends = s.MyBoard.Where(m => m.Kind == Kind.Minion && m.Id != card.Id).ToList();
			switch(kind)
			{
				case TargetKind.EnemyAny:
				case TargetKind.Unknown:
					return s.OppHero != null && !s.OppHero.Immune ? s.OppHero : enemies.OrderBy(e => e.Value).FirstOrDefault();
				case TargetKind.EnemyMinion:
					return enemies.OrderBy(e => e.Value).FirstOrDefault() ?? friends.OrderBy(m => m.Value).FirstOrDefault();
				case TargetKind.FriendlyMinion:
					return friends.OrderByDescending(m => m.Value).FirstOrDefault();
				case TargetKind.FriendlyAny:
					return s.MyHero;
				default:
					return s.OppHero;
			}
		}

		// ───────── 英雄技能 ─────────
		private static BotAction UseHeroPower(Snapshot s, ISet<int> skip)
		{
			var hp = s.MyHeroPower;
			if(hp == null || hp.Exhausted || skip.Contains(hp.Id) || hp.Cost > s.Mana)
				return null;
			if(hp.Target == TargetKind.Unknown)
				return null;
			Ent target = null;
			if(hp.Target != TargetKind.None)
			{
				target = PickTarget(s, hp, hp.Target);
				if(target == null)
					return null;
			}
			// 召喚類英雄技能：板面滿了就不用
			if(hp.Text.ToLowerInvariant().Contains("summon") && s.MyBoard.Count >= 7)
				return null;
			return new BotAction
			{
				Type = ActionType.HeroPower, Source = hp, Target = target,
				Describe = target != null ? $"英雄技能 → {target.Name}" : "英雄技能"
			};
		}
	}
}
