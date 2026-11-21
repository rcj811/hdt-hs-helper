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
	/// <summary>評分權重（預設值由模擬器中的演化演算法調整，起點參考 SilverFish Control 行為）。</summary>
	public class Weights
	{
		// 以下預設值由演化演算法在模擬器中調整（tests/HsHelper.Tests --tune），起點為 SilverFish Control 權重
		public double HeroHp = 1.195, LowHpPenalty = 1.897, LowHpLine = 8.118;
		public double OppHp = 0.948, OppLowBonus = 3.533, OppLowLine = 9.869;
		public double MyMinionBase = 4.817, MyMinionAtk = 2.195, MyMinionHp = 2.001;
		public double OppMinionBase = 4.267, OppMinionAtk = 2.082, OppMinionHp = 1.243, OppAtk4 = 9.338, OppAtk7 = 50, OppTaunt = 3.593;
		public double HandCard = 5.579, ManaLeft = 4.149, Weapon = 1;
		public double LethalRisk = 353.709;      // 對手下回合能斬殺我
		public double AggroFace = 2.077;       // 進攻模式：對手英雄血量的權重倍率
		public bool EnemyTurn = true;        // 是否模擬對手下回合反擊（SilverFish 的 EnemyTurnSimulator 概念）
		public double EnemyTurnBlend = 0.457;  // 反擊後評分所佔比重
		public int RerankTop = 30;           // 只對前 N 個候選局面做反擊模擬
		public double Overextend = 0;        // 學習：對手常用 AOE 時，場上超過 3 隻手下每隻扣分
		public double DangerLine = 1.0, DangerPenalty = 1.5;      // 危險線＝敵方場面攻擊＋爆發 × 這個倍率（v0.9.7）
		public double EnemyBurst = 2;        // 學習：對手手牌／技能的預估額外傷害
		public int EnemyAoeDmg = 0;          // 職業模組：對手下回合可能的清場傷害（v0.9.5）
		public double EnemyAoeChance = 0;    // 職業模組：對手有清場牌的機率

		public Weights Clone() => (Weights)MemberwiseClone();
	}

	public static class Search
	{
		public static int MaxDepth = 12;
		public static int BeamWidth = 200;
		public static Weights W = new Weights();

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
			public int Mana, ManaGained, ManaSpent;
			public int DeckLeft = 99, Fatigue; // 牌庫剩幾張、已受疲勞（v0.9.9 實戰：牌庫空了還一直生命分流）
			public bool HeroPowerUsable;
			public Ent HeroPower;
			public double Bonus;            // 看不懂效果的牌：用費用估值
			public List<Move> Moves = new List<Move>();
			public int NextTokenId = -1;
			public bool Aggro;              // 「誰是打臉方」：這回合採進攻模式

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

			var w = W;
			root.Aggro = IsBeatdown(root);
			var pool = new List<(S st, double score)> { (root, Evaluate(root, w)) };
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
					next[key] = (child, Evaluate(child, w));
				}
				var ranked = next.Values.OrderByDescending(x => x.score).ToList();
				layer = ranked.Take(BeamWidth).Select(x => x.st).ToList();
				pool.AddRange(ranked.Take(w.RerankTop));
			}

			// 最後挑選：前幾名候選再模擬一次「對手下回合反擊」，用反擊後的局面重新評分
			var top = pool.OrderByDescending(x => x.score).Take(w.RerankTop).ToList();
			var best = top[0].st;
			if(w.EnemyTurn && top[0].score < 5e5)
			{
				var bestScore = double.MinValue;
				foreach(var (st, sc) in top)
				{
					var final = st.Opp.Hp <= 0 ? sc : (1 - w.EnemyTurnBlend) * sc + w.EnemyTurnBlend * Evaluate(EnemyResponse(st, w), w, true);
					if(final > bestScore)
					{
						bestScore = final;
						best = st;
					}
				}
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
				ManaSpent = s.ManaSpent,
				DeckLeft = s.DeckCount >= 0 ? s.DeckCount : 99,
				Fatigue = s.Fatigue,
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
			if(a.Kind == Kind.Minion && a.Rush && !a.Charge && a.TurnsInPlay == 0)
			{
				minionsOnly = true; // 突襲：上場當回合只能打手下（實測突襲手下沒有疲勞標記，要先判斷）
				return left;
			}
			return !a.Exhausted || a.Charge ? left : 0;
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
				n.ManaSpent += c.Cost;
				n.Moves.Add(new Move { Type = ActionType.Play, Source = c.Id, Target = target });
				// v0.9.8：0 費牌不花水晶，只要效果不傷自己就該用——補回「少一張手牌」的扣分
				// （例：0 費 +1/+1 增益只值約 4 分 < 手牌 5.6 分，原本會一直留著）
				if(c.Cost == 0 && !c.IsCoin && c.ManaGain == 0 && c.Discard == 0 && c.SelfDmg == 0)
					n.Bonus += W.HandCard * 0.6;
				if(c.Discard > 0 && n.Hand.Count > 0)
				{
					// 棄牌（v0.9.3 實戰：連下兩張末日守衛棄掉 4 張，後期沒牌輸掉）
					// 悲觀假設棄掉最貴的牌（之後不能再規劃打出它）；評分本來就會少一張手牌的分，再加扣那張牌本身的價值
					// → 搜尋自然會「先把別的牌打完，最後才下棄牌的牌」；手牌空了再下就不扣分
					var k = Math.Min(c.Discard, n.Hand.Count);
					foreach(var x in n.Hand.OrderByDescending(h => h.Cost).Take(k).ToList())
					{
						n.Hand.Remove(x);
						n.Bonus -= 3 + x.Cost * 1.5;
					}
				}
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

			// 加水晶法術（啟動、偽造的幸運幣、凜冬號角…）：跟幸運幣一樣當作「多 N 點水晶」，
			// 只有能換到更多出牌時才值得用（沒用完的水晶會在評分扣分）
			if(c.Kind == Kind.Spell && c.ManaGain > 0)
			{
				var n = Base();
				var gain = c.ManaRefresh ? Math.Min(c.ManaGain, n.ManaSpent) : c.ManaGain;
				if(gain <= 0)
					yield break;
				n.Mana += gain;
				n.ManaGained += gain;
				n.Bonus -= 1; // 少一張手牌已經在評分扣過，這裡只輕微懲罰
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
					if(c.HpPerHand > 0) m.Hp += c.HpPerHand * n.Hand.Count; // 暮光飛龍：手牌越多越肥（所以先下它、再出別的牌）
					ApplyExtras(n, c); // 戰吼：召喚、抽牌、護甲、全體增益、打臉（先套用，新下的手下不吃全體增益）
					if(n.My.Count >= 7) n.My.RemoveAt(n.My.Count - 1); // 召喚佔滿時保留本體的位置
					if(c.Charge) m.AttacksLeft = c.MaxAttacks;
					else if(c.Rush) { m.AttacksLeft = c.MaxAttacks; m.MinionsOnly = true; }
					n.My.Add(m);
					var bc = c.Cost * 1.5 + (c.Target != TargetKind.None ? c.Damage * 2 + 2 : 0); // 戰吼等額外效果的粗估
					n.Bonus += double.IsNaN(c.Learned) ? bc : Math.Max(bc, c.Learned); // 實戰觀察過的戰吼效果
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
			if(c.Target == TargetKind.Unknown || c.Special == "twoTargets")
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
					// 「所有角色／所有敵人」連英雄一起打（地獄烈焰會打到自己的英雄）
					if(!isDestroy && (text.IndexOf("characters", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("enemies", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						DamageHero(n.Opp, c.Damage);
						if(!enemyOnly) DamageHero(n.Me, c.Damage);
					}
					RemoveDead(n);
				}
				else
				{
					// v0.9.5：讀卡認得的效果（召喚、抽牌、護甲、全體增益、打臉）直接模擬，局面評分會算到
					var known = ApplyExtras(n, c);
					// 看不懂效果的法術：價值至少比「留在手上」高一點（v0.9.1：0 費法術原本只值 4 分 < 手牌 5.6 分，一直不出）
					var guess = known ? c.Cost * 1.0 + 1 : Math.Max(4 + c.Cost * 3, W.HandCard + 1 + c.Cost * 3);
					// v0.9.4：實戰觀察過的牌，用觀察到的價值（只往上調：延遲效果的牌當下看不出效果，不能因此變成廢牌）
					n.Bonus += double.IsNaN(c.Learned) ? guess : Math.Max(guess, c.Learned);
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

		/// <summary>抽牌：牌庫還有就進手牌（手牌滿了燒掉）；牌庫空了改成疲勞扣血（1、2、3…遞增）。</summary>
		private static void DrawCards(S n, int count, string name = "抽到的牌")
		{
			for(var i = 0; i < count; i++)
			{
				if(n.DeckLeft <= 0)
				{
					n.Fatigue++;
					DamageHero(n.Me, n.Fatigue);
					continue;
				}
				n.DeckLeft--;
				if(n.Hand.Count < 10)
					n.Hand.Add(new Ent { Id = -50000 - n.Hand.Count, Name = name, Cost = 99, Kind = Kind.Other });
			}
		}

		/// <summary>套用讀卡得到的結構化效果（召喚、抽牌、護甲、全體增益、打臉）。回傳有沒有認出任何效果。</summary>
		private static bool ApplyExtras(S n, Ent c)
		{
			var any = false;
			if(c.BuffAllA > 0 || c.BuffAllH > 0)
			{
				foreach(var m in n.My) { m.Atk += c.BuffAllA; m.Hp += c.BuffAllH; }
				any = true;
			}
			if(c.SumN > 0)
			{
				for(var i = 0; i < c.SumN && n.My.Count < 7; i++)
					n.My.Add(new M { Id = n.NextTokenId--, Atk = c.SumA, Hp = c.SumH });
				any = true;
			}
			if(c.HeroAtk > 0)
			{
				n.Me.Atk += c.HeroAtk;
				if(n.Me.AttacksLeft == 0 && !n.Moves.Any(x => x.Source == n.Me.Id && x.Type == ActionType.Attack)) n.Me.AttacksLeft = 1;
				any = true;
			}
			if(c.Draw + c.Gen > 0)
			{
				// 抽到／發現的牌：放進手牌當作「這回合打不出來的牌」（評分會算手牌數）
				DrawCards(n, c.Draw);
				for(var i = 0; i < c.Gen && n.Hand.Count < 10; i++) // 發現／拿牌不從牌庫抽，不會疲勞
					n.Hand.Add(new Ent { Id = -50000 - n.Hand.Count, Name = "拿到的牌", Cost = 99, Kind = Kind.Other });
				any = true;
			}
			if(c.GainArmor > 0) { n.Me.Armor += c.GainArmor; any = true; }
			if(c.SelfDmg > 0) { DamageHero(n.Me, c.SelfDmg); any = true; } // v0.9.7 實戰：烈焰小鬼原本沒算扣自己 3 血
			if(c.SplitDmg > 0)
			{
				// 隨機分配：平均分給敵方角色（手下＋英雄），1 點 1 點輪流打
				var targets = n.Op.Where(m => !m.Stealth).Select(m => m.Id).Concat(new[] { n.Opp.Id }).ToList();
				for(var i = 0; i < c.SplitDmg; i++)
				{
					var id = targets[i % targets.Count];
					var m = n.Op.FirstOrDefault(x => x.Id == id);
					if(m != null) Hit(m, 1); else DamageHero(n.Opp, 1);
				}
				RemoveDead(n);
				any = true;
			}
			if(c.FaceDmg > 0 && c.Target == TargetKind.None) { DamageHero(n.Opp, c.FaceDmg); any = true; }
			return any;
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
				case TargetKind.AnyMinion:
					foreach(var m in st.Op.Where(m => !m.Stealth && !m.Untargetable)) yield return m.Id;
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
			if(!string.IsNullOrEmpty(c.Special))
			{
				// 手寫規則的特殊牌（v0.9.5）
				var t = om ?? mm;
				switch(c.Special)
				{
					case "swap":
						if(t != null) { var a = t.Atk; t.Atk = t.Hp; t.Hp = a; }
						break;
					case "set11":
						if(om != null) { om.Atk = 1; om.Hp = 1; }
						break;
					case "copy":
						if(t != null && n.My.Count < 7)
							n.My.Add(new M { Id = n.NextTokenId--, Atk = t.Atk, Hp = t.Hp, Taunt = t.Taunt, DS = t.DS, Poison = t.Poison, Lifesteal = t.Lifesteal, Windfury = t.Windfury });
						break;
					case "copyHand":
						if(n.Hand.Count < 10) n.Hand.Add(new Ent { Id = -50000 - n.Hand.Count, Name = "複製的牌", Cost = 99, Kind = Kind.Other });
						if(t != null) n.Bonus += (t.Atk + t.Hp) * 0.5;
						break;
					case "copyDeck":
						if(t != null) n.Bonus += (t.Atk + t.Hp) * 0.4; // 洗進牌庫：之後才拿得到
						break;
					case "aoeOthers":
						foreach(var m in n.Op.Concat(n.My).Where(m => m.Id != tid).ToList()) Hit(m, c.Damage);
						break;
				}
				n.Bonus += c.Cost * 0.5;
				RemoveDead(n);
				return;
			}
			if(c.LearnedTarget)
			{
				// 敘述看不懂、但實戰觀察過：照觀察到的效果套用在目標上
				var t = om ?? mm;
				if(t != null)
				{
					if(c.LKill) t.Hp = 0;
					else { t.Atk = Math.Max(0, t.Atk + c.LAtk); t.Hp += c.LHp; }
				}
				else if(tid == n.Opp.Id && c.LHp < 0) DamageHero(n.Opp, -c.LHp);
				n.Bonus += c.Cost * 0.5;
				RemoveDead(n);
				return;
			}
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
				n.ManaSpent += hp.Cost;
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
				// 生命分流：扣 2 血抽 1 張。牌庫空了＝扣 2 血＋疲勞，沒有任何好處（v0.9.9 實戰：牌庫 0 張還連用兩次）
				s2.Me.Hp -= 2;
				if(s2.DeckLeft <= 0)
				{
					DrawCards(s2, 1);
					s2.Bonus -= 4;
				}
				else
				{
					s2.DeckLeft--;
					// 牌庫快見底時，多抽一張＝早一回合疲勞：價值遞減
					s2.Bonus += s2.DeckLeft >= 5 ? 6 : 6 * (s2.DeckLeft + 1) / 6.0 - 1;
				}
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

		// ───────── 誰是打臉方（Who's the Beatdown）─────────
		/// <summary>
		/// 比較雙方「幾回合能打死對方」：我方比較快、或場面攻擊明顯佔優 → 進攻模式（更重視對手英雄血量）。
		/// </summary>
		private static bool IsBeatdown(S st)
		{
			var myAtk = st.My.Sum(m => m.Atk) + st.Me.Atk;
			var oppAtk = Math.Max(1, st.Op.Sum(m => m.Atk));
			var oppLife = st.Opp.Hp + st.Opp.Armor;
			var myLife = st.Me.Hp + st.Me.Armor;
			var myClock = myAtk <= 0 ? 99 : Math.Ceiling(oppLife / (double)myAtk);
			var oppClock = Math.Ceiling(myLife / (double)oppAtk);
			return myClock < oppClock || (oppLife <= 15 && myAtk >= oppAtk);
		}

		// ───────── 對手回合模擬 ─────────
		/// <summary>
		/// 簡化的對手反擊：對手每隻能攻擊的手下（攻擊力高的先）依序挑「讓我方評分最低」的目標
		/// （有嘲諷只能打嘲諷）；最後假設對手英雄技能／法術再補 2 點傷害。
		/// </summary>
		private static S EnemyResponse(S st, Weights w)
		{
			var n = st.Clone();
			// 回合交換：敵方手下解凍、我方的凍結狀態不影響
			foreach(var m in n.Op) { m.AttacksLeft = m.Frozen ? 0 : (m.Windfury ? 2 : 1); m.Frozen = false; }
			foreach(var m in n.Op.OrderByDescending(m => m.Atk).ToList())
			{
				while(m.AttacksLeft > 0 && m.Atk > 0 && n.Op.Contains(m) && n.Me.Hp > 0)
				{
					var taunts = n.My.Where(x => x.Taunt && !x.Stealth).ToList();
					var targets = taunts.Count > 0 ? taunts.Cast<M>().ToList() : n.My.Where(x => !x.Stealth).ToList();
					S bestState = null;
					var bestVal = double.MaxValue;
					foreach(var t in targets)
					{
						var c = n.Clone();
						EnemyAttack(c, c.Op.First(x => x.Id == m.Id), c.My.First(x => x.Id == t.Id));
						var v = Evaluate(c, w, true);
						if(v < bestVal) { bestVal = v; bestState = c; }
					}
					if(taunts.Count == 0 && !n.Me.Immune)
					{
						var c = n.Clone();
						var a = c.Op.First(x => x.Id == m.Id);
						DamageHero(c.Me, a.Atk);
						a.AttacksLeft--;
						var v = Evaluate(c, w, true);
						if(v < bestVal) { bestVal = v; bestState = c; }
					}
					if(bestState == null)
						break;
					n = bestState;
					m.AttacksLeft--;
				}
			}
			DamageHero(n.Me, (int)Math.Round(w.EnemyBurst)); // 對手手牌／英雄技能的預估傷害（會從過去對局學習）
			return n;
		}

		private static void EnemyAttack(S st, M attacker, M defender)
		{
			var hadDs = defender.DS;
			Hit(defender, attacker.Atk);
			if(!hadDs && attacker.Poison && attacker.Atk > 0) defender.Hp = 0;
			var hadDsA = attacker.DS;
			Hit(attacker, defender.Atk);
			if(!hadDsA && defender.Poison && defender.Atk > 0) attacker.Hp = 0;
			attacker.AttacksLeft--;
			RemoveDead(st);
		}

		// ───────── 評分（權重參考 SilverFish 的 Control 行為，再用演化演算法調整）─────────
		private static double Evaluate(S st, Weights w, bool afterEnemyTurn = false)
		{
			if(st.Opp.Hp <= 0)
				return 1e6 - st.Moves.Count; // 斬殺：越少步越好
			if(st.Me.Hp <= 0)
				return -1e6;
			double v = 0;

			// 我方英雄：低於警戒線開始平方重罰
			var myLife = st.Me.Hp + st.Me.Armor;
			// v0.9.7 實戰（輸給獵人）：危險線跟著敵方場面＋爆發走——敵方場上 16 攻時 19 血並不安全，
			// 這時再用生命分流、烈焰小鬼、地獄烈焰扣自己的血要付出代價
			var line = w.LowHpLine;
			if(!afterEnemyTurn)
			{
				var danger = st.Op.Where(m => !m.Frozen).Sum(m => m.Atk * (m.Windfury ? 2 : 1)) - st.My.Where(m => m.Taunt).Sum(m => m.Hp) * 0.5 + w.EnemyBurst;
				line = Math.Max(line, danger * w.DangerLine);
			}
			v += myLife > w.LowHpLine ? myLife * w.HeroHp : -w.LowHpPenalty * Math.Pow(w.LowHpLine + 1 - myLife, 2);
			if(line > w.LowHpLine && myLife < line)
				v -= w.DangerPenalty * Math.Pow(line - Math.Max(myLife, w.LowHpLine), 2) / Math.Max(1, line); // 危險線以下每少 1 血代價遞增

			// 敵方英雄：低於警戒線越低越加分（壓血）；進攻模式加重
			var oppLife = st.Opp.Hp + st.Opp.Armor;
			var face = st.Aggro ? w.AggroFace : 1.0;
			v += face * (oppLife > w.OppLowLine ? -oppLife * w.OppHp : w.OppLowBonus * (w.OppLowLine + 1 - oppLife));

			v += w.Weapon * st.Me.WeaponAtk * st.Me.WeaponDur;

			foreach(var m in st.My)
			{
				v += w.MyMinionBase + m.Hp * w.MyMinionHp + m.Atk * w.MyMinionAtk;
				if(m.DS) v += 1 + m.Atk * 0.5;
				if(m.Stealth) v += 1;
				if(m.Lifesteal) v += m.Atk / 2.0;
				if(m.Taunt) v += 1 + (m.DS ? 4 : 0);
				if(m.Windfury) v += m.Atk;
				if(m.Poison) v += 2;
			}

			foreach(var m in st.Op)
			{
				double e = w.OppMinionBase + m.Hp * w.OppMinionHp;
				if(!m.Frozen || afterEnemyTurn)
				{
					e += m.Atk * w.OppMinionAtk;
					if(m.Windfury) e += m.Atk * 2;
					if(m.Atk >= 4) e += w.OppAtk4;
					if(m.Atk >= 7) e += w.OppAtk7;
				}
				if(m.Atk == 0) e -= 7;
				if(m.Taunt) e += w.OppTaunt;
				if(m.DS) e += m.Atk;
				if(m.DS && m.Taunt) e += 5;
				if(m.Poison) e += 4 + (st.My.Count < st.Op.Count ? 10 : 0);
				if(m.Lifesteal) e += m.Atk;
				v -= e;
			}

			v -= w.Overextend * Math.Max(0, st.My.Count - 3);
			if(w.EnemyAoeDmg > 0 && w.EnemyAoeChance > 0)
			{
				// 職業模組：對手可能清場 → 會被一次清掉的手下打折（鋪場不要全部押上去）
				double atRisk = 0;
				foreach(var m in st.My.Where(m => !m.DS && m.Hp <= w.EnemyAoeDmg))
					atRisk += w.MyMinionBase + m.Hp * w.MyMinionHp + m.Atk * w.MyMinionAtk;
				v -= w.EnemyAoeChance * atRisk * 1.0;
			}
			v += st.Hand.Count * w.HandCard;
			v += st.Bonus;
			if(!afterEnemyTurn)
				v -= w.ManaLeft * Math.Max(0, st.Mana);

			// 下回合危險：敵方場面攻擊 ≥ 我方血量（扣掉我方嘲諷能擋的）
			if(!afterEnemyTurn)
			{
				var threat = st.Op.Where(m => !m.Frozen).Sum(m => m.Atk * (m.Windfury ? 2 : 1));
				var blocked = st.My.Where(m => m.Taunt).Sum(m => m.Hp);
				if(threat - blocked * 0.5 >= myLife)
					v -= w.LethalRisk;
			}
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
