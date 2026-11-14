using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HsHelper.Bot
{
	/// <summary>
	/// 回合搜尋 AI（概念參考 SilverFish / Hearthbuddy：模擬整個回合的出牌＋攻擊順序，用評分函式挑最好的一條）。
	/// 模擬器是簡化版：精準處理攻擊、嘲諷、聖盾、劇毒、吸血、突襲／衝鋒、武器、傷害法術；
	/// 看不懂的卡牌效果用「費用估值」近似。每次只執行最佳路線的第一步，執行完重新讀狀態再搜尋。
	/// </summary>
	public static class Search
	{
		public const int MaxDepth = 12;
		public const int BeamWidth = 200;

		// ───────── 模擬狀態 ─────────
		private class M // 手下
		{
			public int Id, Atk, Hp, AttacksLeft;
			public bool Taunt, DS, Poison, Lifesteal, Frozen, Stealth, Untargetable, MinionsOnly, Windfury;
			public M Clone() => (M)MemberwiseClone();
		}

		private class H // 英雄
		{
			public int Id, Hp, Armor, Atk, AttacksLeft, WeaponAtk, WeaponDur;
			public bool Immune;
			public H Clone() => (H)MemberwiseClone();
		}

		private class Move
		{
			public ActionType Type;
			public int Source;
			public int Target; // 0 = 無
		}

		private class S
		{
			public H Me, Opp;
			public List<M> My = new List<M>(), Op = new List<M>();
			public List<Ent> Hand = new List<Ent>();
			public int Mana, ManaGained;
			public bool HeroPowerUsable;
			public Ent HeroPower;
			public double Bonus;            // 看不懂效果的牌：用費用估值
			public List<Move> Moves = new List<Move>();
			public int NextTokenId = -1;

			public S Clone()
			{
				var c = (S)MemberwiseClone();
				c.Me = Me.Clone();
				c.Opp = Opp.Clone();
				c.My = My.Select(m => m.Clone()).ToList();
				c.Op = Op.Select(m => m.Clone()).ToList();
				c.Hand = new List<Ent>(Hand);
				c.Moves = new List<Move>(Moves);
				return c;
			}

			public string Key()
			{
				var sb = new StringBuilder();
				sb.Append(Me.Hp).Append(',').Append(Me.Armor).Append(',').Append(Me.AttacksLeft).Append(',').Append(Me.WeaponDur).Append('|');
				sb.Append(Opp.Hp).Append(',').Append(Opp.Armor).Append('|').Append(Mana).Append(HeroPowerUsable ? 'h' : '-').Append('|');
				foreach(var m in My) sb.Append(m.Id).Append(':').Append(m.Hp).Append(':').Append(m.Atk).Append(':').Append(m.AttacksLeft).Append(m.DS ? 'd' : '-').Append(';');
				sb.Append('|');
				foreach(var m in Op) sb.Append(m.Id).Append(':').Append(m.Hp).Append(m.DS ? 'd' : '-').Append(m.Frozen ? 'f' : '-').Append(';');
				sb.Append('|');
				foreach(var c in Hand.OrderBy(c => c.Id)) sb.Append(c.Id).Append(',');
				return sb.ToString();
			}
		}

		// ───────── 入口 ─────────
		public static BotAction BestFirstAction(Snapshot snap, ISet<int> skip, out string plan)
		{
			plan = "";
			var root = Build(snap, skip);
			if(root == null)
				return null;

			var best = root;
			var bestScore = Evaluate(root);
			var layer = new List<S> { root };
			for(var depth = 0; depth < MaxDepth && layer.Count > 0; depth++)
			{
				var next = new Dictionary<string, (S st, double score)>();
				foreach(var st in layer)
				foreach(var child in Expand(st))
				{
					var key = child.Key();
					if(next.ContainsKey(key))
						continue;
					var sc = Evaluate(child);
					next[key] = (child, sc);
					if(sc > bestScore)
					{
						bestScore = sc;
						best = child;
					}
				}
				layer = next.Values.OrderByDescending(x => x.score).Take(BeamWidth).Select(x => x.st).ToList();
			}

			if(best.Moves.Count == 0)
				return null; // 什麼都不做最好 → 結束回合
			plan = string.Join(" → ", best.Moves.Select(m => Describe(snap, m)));
			return ToAction(snap, best.Moves[0]);
		}

		// ───────── 建立初始狀態 ─────────
		private static S Build(Snapshot s, ISet<int> skip)
		{
			if(s.MyHero == null || s.OppHero == null)
				return null;
			var st = new S
			{
				Me = new H
				{
					Id = s.MyHero.Id, Hp = s.MyHero.Hp, Armor = s.MyHero.Armor, Atk = s.MyHero.Atk,
					Immune = s.MyHero.Immune,
					AttacksLeft = !skip.Contains(s.MyHero.Id) && !s.MyHero.Frozen && !s.MyHero.CantAttack
					              && s.MyHero.AttacksThisTurn < s.MyHero.MaxAttacks ? 1 : 0,
					WeaponAtk = s.MyWeapon?.Atk ?? 0,
					WeaponDur = s.MyWeapon?.Durability ?? 0
				},
				Opp = new H { Id = s.OppHero.Id, Hp = s.OppHero.Hp, Armor = s.OppHero.Armor, Immune = s.OppHero.Immune },
				Mana = s.Mana,
				HeroPower = s.MyHeroPower,
				HeroPowerUsable = s.MyHeroPower != null && !s.MyHeroPower.Exhausted && !skip.Contains(s.MyHeroPower.Id)
			};
			foreach(var e in s.MyBoard.Where(e => e.Kind == Kind.Minion))
			{
				var m = ToM(e);
				var left = AttacksLeft(e, out var mo);
				m.AttacksLeft = skip.Contains(e.Id) ? 0 : left;
				m.MinionsOnly = mo;
				st.My.Add(m);
			}
			foreach(var e in s.OppBoard.Where(e => e.Kind == Kind.Minion))
				st.Op.Add(ToM(e));
			st.Hand = s.Hand.Where(c => !skip.Contains(c.Id)).ToList();
			return st;
		}

		private static M ToM(Ent e) => new M
		{
			Id = e.Id, Atk = e.Atk, Hp = e.Hp, Taunt = e.Taunt, DS = e.DivineShield, Poison = e.Poisonous,
			Lifesteal = e.Lifesteal, Frozen = e.Frozen, Stealth = e.Stealth, Windfury = e.Windfury,
			Untargetable = e.Immune || e.Dormant || e.Untouchable
		};

		private static int AttacksLeft(Ent a, out bool minionsOnly)
		{
			minionsOnly = false;
			if(a.Atk <= 0 || a.Frozen || a.CantAttack || a.Dormant)
				return 0;
			var left = a.MaxAttacks - a.AttacksThisTurn;
			if(left <= 0)
				return 0;
			if(!a.Exhausted || a.Charge)
				return left;
			if(a.Rush && a.TurnsInPlay == 0 && a.AttacksThisTurn == 0)
			{
				minionsOnly = true;
				return left;
			}
			return 0;
		}

		// ───────── 產生下一步 ─────────
		private static IEnumerable<S> Expand(S st)
		{
			// 出牌（同名同費的牌只試一張，減少分支）
			var seen = new HashSet<string>();
			foreach(var c in st.Hand)
			{
				if(c.Cost > st.Mana || !seen.Add(c.CardId + "#" + c.Cost))
					continue;
				foreach(var child in PlayCard(st, c))
					yield return child;
			}

			// 英雄技能
			if(st.HeroPowerUsable && st.HeroPower != null && st.HeroPower.Cost <= st.Mana)
				foreach(var child in UseHeroPower(st))
					yield return child;

			// 攻擊
			var taunts = st.Op.Where(m => m.Taunt && !m.Stealth && !m.Untargetable).ToList();
			var attackers = st.My.Where(m => m.AttacksLeft > 0 && m.Atk > 0).Select(m => m.Id).ToList();
			if(st.Me.AttacksLeft > 0 && st.Me.Atk > 0)
				attackers.Add(st.Me.Id);
			foreach(var aid in attackers)
			{
				var minionsOnly = aid != st.Me.Id && st.My.First(m => m.Id == aid).MinionsOnly;
				var targets = taunts.Count > 0
					? taunts.Select(t => t.Id).ToList()
					: st.Op.Where(m => !m.Stealth && !m.Untargetable).Select(m => m.Id).ToList();
				if(taunts.Count == 0 && !minionsOnly && !st.Opp.Immune)
					targets.Add(st.Opp.Id);
				foreach(var tid in targets)
				{
					var c = st.Clone();
					DoAttack(c, aid, tid);
					c.Moves.Add(new Move { Type = ActionType.Attack, Source = aid, Target = tid });
					yield return c;
				}
			}
		}

		private static readonly Regex Buff = new Regex(@"\+(\d+)/\+(\d+)", RegexOptions.Compiled);
		private static readonly Regex Restore = new Regex(@"restore (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex Armor = new Regex(@"(\d+) armor", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex Draw = new Regex(@"draw (a|\d+) cards?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static IEnumerable<S> PlayCard(S st, Ent c)
		{
			S Base(int target = 0)
			{
				var n = st.Clone();
				n.Hand.Remove(c);
				n.Mana -= c.Cost;
				n.Moves.Add(new Move { Type = ActionType.Play, Source = c.Id, Target = target });
				return n;
			}

			if(c.IsCoin)
			{
				var n = Base();
				n.Mana += 1;
				n.ManaGained++;
				n.Bonus -= 4; // 不必要時別亂用幸運幣（SilverFish 也會懲罰）
				yield return n;
				yield break;
			}

			switch(c.Kind)
			{
				case Kind.Minion:
				{
					if(st.My.Count >= 7)
						yield break;
					var n = Base();
					var m = new M
					{
						Id = c.Id, Atk = c.Atk, Hp = c.Hp, Taunt = c.Taunt, DS = c.DivineShield, Poison = c.Poisonous,
						Lifesteal = c.Lifesteal, Stealth = c.Stealth, Windfury = c.Windfury
					};
					if(c.Charge) m.AttacksLeft = c.MaxAttacks;
					else if(c.Rush) { m.AttacksLeft = c.MaxAttacks; m.MinionsOnly = true; }
					n.My.Add(m);
					n.Bonus += c.Cost * 1.5 + (c.Target != TargetKind.None ? c.Damage * 2 + 2 : 0); // 戰吼等額外效果的粗估
					yield return n;
					yield break;
				}
				case Kind.Weapon:
				{
					var n = Base();
					n.Me.Atk = n.Me.Atk - n.Me.WeaponAtk + c.Atk;
					n.Me.WeaponAtk = c.Atk;
					n.Me.WeaponDur = Math.Max(1, c.Durability > 0 ? c.Durability : c.Hp);
					n.Bonus += 2;
					yield return n;
					yield break;
				}
				case Kind.Hero:
				{
					var n = Base();
					n.Me.Armor += 5;
					n.Bonus += c.Cost * 5;
					yield return n;
					yield break;
				}
				case Kind.Spell:
					break;
				default:
					yield break;
			}

			// 法術
			var text = c.Text ?? "";
			if(c.Target == TargetKind.Unknown)
				yield break;
			if(c.Target == TargetKind.None)
			{
				var n = Base();
				if(c.AoE)
				{
					var enemyOnly = text.IndexOf("enemy", StringComparison.OrdinalIgnoreCase) >= 0;
					var isDestroy = c.Damage == 0;
					foreach(var m in n.Op) Hit(m, isDestroy ? 999 : c.Damage);
					if(!enemyOnly) foreach(var m in n.My) Hit(m, isDestroy ? 999 : c.Damage);
					RemoveDead(n);
				}
				else
				{
					var d = Draw.Match(text);
					var draws = d.Success ? (d.Groups[1].Value == "a" ? 1 : int.Parse(d.Groups[1].Value)) : 0;
					n.Bonus += 4 + c.Cost * 3 + draws * 3;
					var ar = Armor.Match(text);
					if(ar.Success) n.Me.Armor += int.Parse(ar.Groups[1].Value);
					if(c.Damage > 0 && text.IndexOf("enemy hero", StringComparison.OrdinalIgnoreCase) >= 0)
						n.Opp.Hp -= c.Damage;
				}
				yield return n;
				yield break;
			}

			foreach(var tid in TargetsFor(st, c.Target))
			{
				var n = Base(tid);
				ApplyTargeted(n, c, tid);
				yield return n;
			}
		}

		private static IEnumerable<int> TargetsFor(S st, TargetKind kind)
		{
			switch(kind)
			{
				case TargetKind.EnemyAny:
					foreach(var m in st.Op.Where(m => !m.Stealth && !m.Untargetable)) yield return m.Id;
					if(!st.Opp.Immune) yield return st.Opp.Id;
					break;
				case TargetKind.EnemyMinion:
					foreach(var m in st.Op.Where(m => !m.Stealth && !m.Untargetable)) yield return m.Id;
					break;
				case TargetKind.FriendlyMinion:
					foreach(var m in st.My) yield return m.Id;
					break;
				case TargetKind.FriendlyAny:
					yield return st.Me.Id;
					foreach(var m in st.My) yield return m.Id;
					break;
			}
		}

		private static void ApplyTargeted(S n, Ent c, int tid)
		{
			var text = c.Text ?? "";
			var lower = text.ToLowerInvariant();
			var om = n.Op.FirstOrDefault(m => m.Id == tid);
			var mm = n.My.FirstOrDefault(m => m.Id == tid);
			if(c.Damage > 0)
			{
				if(om != null) Hit(om, c.Damage);
				else if(tid == n.Opp.Id) DamageHero(n.Opp, c.Damage);
			}
			else if(om != null)
			{
				if(lower.Contains("destroy") || lower.Contains("return")) om.Hp = 0;
				else if(lower.Contains("transform")) { om.Atk = 1; om.Hp = 1; om.Taunt = om.DS = om.Poison = om.Lifesteal = false; }
				else if(lower.Contains("silence")) { om.Taunt = om.DS = om.Poison = om.Lifesteal = om.Windfury = false; }
				else if(lower.Contains("freeze")) om.Frozen = true;
			}
			var b = Buff.Match(text);
			if(b.Success && mm != null)
			{
				mm.Atk += int.Parse(b.Groups[1].Value);
				mm.Hp += int.Parse(b.Groups[2].Value);
			}
			var r = Restore.Match(text);
			if(r.Success)
			{
				if(tid == n.Me.Id) n.Me.Hp = Math.Min(30, n.Me.Hp + int.Parse(r.Groups[1].Value));
			}
			if(!b.Success && !r.Success && c.Damage == 0 && om == null)
				n.Bonus += c.Cost * 2; // 其他友方效果
			n.Bonus += c.Cost * 0.5;
			RemoveDead(n);
		}

		private static IEnumerable<S> UseHeroPower(S st)
		{
			var hp = st.HeroPower;
			var text = hp.Text ?? "";
			var lower = text.ToLowerInvariant();

			S Base(int target = 0)
			{
				var n = st.Clone();
				n.Mana -= hp.Cost;
				n.HeroPowerUsable = false;
				n.Moves.Add(new Move { Type = ActionType.HeroPower, Source = hp.Id, Target = target });
				return n;
			}

			if(hp.Target == TargetKind.Unknown)
				yield break;
			if(hp.Target != TargetKind.None)
			{
				foreach(var tid in TargetsFor(st, hp.Target))
				{
					var n = Base(tid);
					ApplyTargeted(n, hp, tid);
					n.Bonus -= hp.Cost * 0.5;
					yield return n;
				}
				yield break;
			}

			var s2 = Base();
			if(hp.Damage > 0 && lower.Contains("enemy hero")) DamageHero(s2.Opp, hp.Damage);
			else if(lower.Contains("summon"))
			{
				if(st.My.Count >= 7) yield break;
				s2.My.Add(new M { Id = s2.NextTokenId--, Atk = 1, Hp = 1 });
			}
			else if(Armor.IsMatch(text))
			{
				s2.Me.Armor += int.Parse(Armor.Match(text).Groups[1].Value);
				if(lower.Contains("attack")) { s2.Me.Atk += 1; if(s2.Me.AttacksLeft == 0 && !st.Moves.Any(m => m.Source == st.Me.Id)) s2.Me.AttacksLeft = 1; }
			}
			else if(lower.Contains("+1 attack") || lower.Contains("+2 attack"))
			{
				s2.Me.Atk += lower.Contains("+2") ? 2 : 1;
				if(!st.Moves.Any(m => m.Source == st.Me.Id && m.Type == ActionType.Attack)) s2.Me.AttacksLeft = 1;
			}
			else if(lower.Contains("equip"))
			{
				s2.Me.Atk = s2.Me.Atk - s2.Me.WeaponAtk + 1;
				s2.Me.WeaponAtk = 1; s2.Me.WeaponDur = 2;
			}
			else if(lower.Contains("draw"))
			{
				s2.Me.Hp -= 2;
				s2.Bonus += 6;
			}
			else
				s2.Bonus += 4;
			yield return s2;
		}

		// ───────── 戰鬥規則 ─────────
		private static void Hit(M m, int dmg)
		{
			if(dmg <= 0) return;
			if(m.DS) { m.DS = false; return; }
			m.Hp -= dmg;
		}

		private static void DamageHero(H h, int dmg)
		{
			if(h.Immune || dmg <= 0) return;
			var a = Math.Min(h.Armor, dmg);
			h.Armor -= a;
			h.Hp -= dmg - a;
		}

		private static void DoAttack(S st, int aid, int tid)
		{
			var heroAttacks = aid == st.Me.Id;
			var am = heroAttacks ? null : st.My.First(m => m.Id == aid);
			var atk = heroAttacks ? st.Me.Atk : am.Atk;
			var lifesteal = am?.Lifesteal ?? false;

			if(tid == st.Opp.Id)
			{
				DamageHero(st.Opp, atk);
			}
			else
			{
				var d = st.Op.First(m => m.Id == tid);
				var hadDs = d.DS;
				Hit(d, atk);
				if(!hadDs && am != null && am.Poison && atk > 0) d.Hp = 0;
				if(heroAttacks)
					DamageHero(st.Me, d.Atk);
				else
				{
					var hadDsA = am.DS;
					Hit(am, d.Atk);
					if(!hadDsA && d.Poison && d.Atk > 0) am.Hp = 0;
				}
			}
			if(lifesteal) st.Me.Hp = Math.Min(30, st.Me.Hp + atk);

			if(heroAttacks)
			{
				st.Me.AttacksLeft--;
				if(st.Me.WeaponDur > 0 && --st.Me.WeaponDur == 0)
				{
					st.Me.Atk -= st.Me.WeaponAtk;
					st.Me.WeaponAtk = 0;
				}
			}
			else
			{
				am.AttacksLeft--;
				am.Stealth = false;
			}
			RemoveDead(st);
		}

		private static void RemoveDead(S st)
		{
			st.My.RemoveAll(m => m.Hp <= 0);
			st.Op.RemoveAll(m => m.Hp <= 0);
		}

		// ───────── 評分（權重參考 SilverFish 的 Control 行為）─────────
		private static double Evaluate(S st)
		{
			if(st.Opp.Hp <= 0)
				return 1e6 - st.Moves.Count; // 斬殺：越少步越好
			double v = 0;

			// 我方英雄：10 血以下開始重罰
			var myLife = st.Me.Hp + st.Me.Armor;
			v += myLife > 10 ? myLife : -2 * Math.Pow(11 - myLife, 2);

			// 敵方英雄：11 血以下，越低越加分（壓血）
			var oppLife = st.Opp.Hp + st.Opp.Armor;
			v += oppLife > 11 ? -oppLife : 4 * (12 - oppLife);

			// 武器
			v += st.Me.WeaponAtk * st.Me.WeaponDur;

			// 我方手下
			foreach(var m in st.My)
			{
				v += 5 + m.Hp * 2 + m.Atk * 2;
				if(m.DS) v += 1;
				if(m.Stealth) v += 1;
				if(m.Lifesteal) v += m.Atk / 2.0;
				if(m.DS && m.Taunt) v += 4;
				if(m.Windfury) v += m.Atk;
			}

			// 敵方手下（高攻擊威脅加重）
			foreach(var m in st.Op)
			{
				double e = 5 + m.Hp * 2;
				if(!m.Frozen)
				{
					e += m.Atk * 2;
					if(m.Windfury) e += m.Atk * 2;
					if(m.Atk >= 4) e += 10;
					if(m.Atk >= 7) e += 50;
				}
				if(m.Atk == 0) e -= 7;
				if(m.Taunt) e += 5;
				if(m.DS) e += m.Atk;
				if(m.DS && m.Taunt) e += 5;
				if(m.Poison) e += 4 + (st.My.Count < st.Op.Count ? 10 : 0);
				if(m.Lifesteal) e += m.Atk;
				v -= e;
			}

			// 手牌、剩餘法力
			v += st.Hand.Count * 5;
			v += st.Bonus;
			v -= 5 * Math.Max(0, st.Mana);

			// 下回合危險：敵方場面攻擊 ≥ 我方血量（扣掉我方嘲諷能擋的）
			var threat = st.Op.Where(m => !m.Frozen).Sum(m => m.Atk * (m.Windfury ? 2 : 1));
			var blocked = st.My.Where(m => m.Taunt).Sum(m => m.Hp);
			if(threat - blocked * 0.5 >= myLife)
				v -= 300;
			return v;
		}

		// ───────── 轉成實際動作 ─────────
		private static Ent Find(Snapshot s, int id) =>
			s.Hand.Concat(s.MyBoard).Concat(s.OppBoard)
				.Concat(new[] { s.MyHero, s.OppHero, s.MyHeroPower })
				.FirstOrDefault(e => e != null && e.Id == id);

		private static BotAction ToAction(Snapshot s, Move m)
		{
			var src = Find(s, m.Source);
			var tgt = m.Target != 0 ? Find(s, m.Target) : null;
			if(src == null)
				return null;
			return new BotAction { Type = m.Type, Source = src, Target = tgt, Describe = Describe(s, m) };
		}

		private static string Describe(Snapshot s, Move m)
		{
			string N(int id) => id < 0 ? "衍生物" : Find(s, id)?.Name ?? "新手下";
			switch(m.Type)
			{
				case ActionType.Play: return m.Target != 0 ? $"打出 {N(m.Source)}→{N(m.Target)}" : $"打出 {N(m.Source)}";
				case ActionType.HeroPower: return m.Target != 0 ? $"英雄技能→{N(m.Target)}" : "英雄技能";
				case ActionType.Attack: return $"{N(m.Source)} 攻擊 {N(m.Target)}";
				default: return "結束回合";
			}
		}
	}
}
