using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace HsHelper.Bot
{
	/// <summary>
	/// 實戰觀察學到的卡牌效果（v0.9.4）。
	/// AI 用卡牌英文敘述判斷效果，看不懂的牌原本只能用費用粗估、或乾脆不出。
	/// 現在每次打出法術／英雄技能／戰吼手下，都比對出牌前後的局面，記下「實際發生了什麼」，
	/// 下次規劃時用觀察到的效果來估價值、決定目標。資料存在學習資料庫，可匯出／匯入。
	/// </summary>
	[DataContract]
	public class CardLesson
	{
		[DataMember(Order = 1)] public string CardId = "";
		[DataMember(Order = 2)] public string Name = "";
		[DataMember(Order = 3)] public int Tries;               // 觀察次數（有結算的）
		[DataMember(Order = 4)] public int Target = -1;         // 學到的目標類型（TargetKind），-1 = 還不知道
		[DataMember(Order = 5)] public double Value;            // 平均效果價值（評分單位）
		[DataMember(Order = 6)] public double TgtAtk, TgtHp;    // 對目標造成的平均攻擊／血量變化
		[DataMember(Order = 7)] public double KillRate;         // 目標被消滅的比例
		[DataMember(Order = 8)] public int EnemyTries, FriendlyTries, NoEffect; // 試目標的紀錄
		[DataMember(Order = 9)] public string Utc = DateTime.UtcNow.ToString("o");
		[DataMember(Order = 10)] public string Summary = "";
	}

	/// <summary>一次出牌觀察到的效果。</summary>
	public class Effect
	{
		public double EnemyStatLoss;   // 敵方手下少掉的攻擊＋血量（死掉的整隻算）
		public int Kills;              // 敵方手下死亡數
		public int OppFace;            // 對手英雄少掉的血＋護甲
		public int MyHeroLoss;         // 我方英雄少掉的血＋護甲
		public int MyHeroGain;         // 我方英雄多出的血＋護甲
		public double FriendlyGain;    // 我方原有手下多出的攻擊＋血量
		public double FriendlyLoss;    // 我方原有手下少掉的攻擊＋血量
		public double Summoned;        // 新出現的我方手下（不含打出的那張）攻擊＋血量
		public int Draw;               // 多拿到的手牌
		public int TargetSide;         // 選的目標：+1 敵方、-1 我方、0 無
		public int TgtAtk, TgtHp;      // 目標的攻擊／血量變化
		public bool TgtKilled, TgtChanged;

		public double Value(Weights w) =>
			EnemyStatLoss * 1.5 + Kills * 3 + OppFace * 1.0 + MyHeroGain * 0.5 - MyHeroLoss * 0.6
			+ FriendlyGain * 1.5 - FriendlyLoss * 1.5 + Summoned * 1.5 + Draw * (w?.HandCard ?? 5.5);

		public bool Visible => EnemyStatLoss > 0 || Kills > 0 || OppFace != 0 || MyHeroLoss != 0 || MyHeroGain != 0
		                       || FriendlyGain > 0 || FriendlyLoss > 0 || Summoned > 0 || Draw != 0 || TgtChanged;
	}

	public static class Lessons
	{
		private static int Life(Ent h) => h == null ? 0 : h.Hp + h.Armor;

		/// <summary>比對出牌前後的局面，算出這張牌實際的效果。</summary>
		public static Effect Observe(Snapshot before, Snapshot after, Ent card, Ent target)
		{
			var e = new Effect();
			if(before == null || after == null)
				return e;

			foreach(var m in before.OppBoard.Where(x => x.Kind == Kind.Minion))
			{
				var now = after.OppBoard.FirstOrDefault(x => x.Id == m.Id);
				if(now == null) { e.Kills++; e.EnemyStatLoss += m.Atk + m.Hp; }
				else e.EnemyStatLoss += Math.Max(0, m.Atk - now.Atk) + Math.Max(0, m.Hp - now.Hp);
			}
			foreach(var m in before.MyBoard.Where(x => x.Kind == Kind.Minion))
			{
				var now = after.MyBoard.FirstOrDefault(x => x.Id == m.Id);
				if(now == null) { e.FriendlyLoss += m.Atk + m.Hp; continue; }
				e.FriendlyGain += Math.Max(0, now.Atk - m.Atk) + Math.Max(0, now.Hp - m.Hp);
				e.FriendlyLoss += Math.Max(0, m.Atk - now.Atk) + Math.Max(0, m.Hp - now.Hp);
			}
			var oldIds = new HashSet<int>(before.MyBoard.Select(x => x.Id));
			e.Summoned = after.MyBoard.Where(x => x.Kind == Kind.Minion && !oldIds.Contains(x.Id) && x.Id != card.Id)
				.Sum(x => x.Atk + x.Hp);

			e.OppFace = Life(before.OppHero) - Life(after.OppHero);
			var my = Life(after.MyHero) - Life(before.MyHero);
			if(my > 0) e.MyHeroGain = my; else e.MyHeroLoss = -my;
			var handBefore = before.Hand.Count - (before.Hand.Any(h => h.Id == card.Id) ? 1 : 0);
			e.Draw = after.Hand.Count - handBefore;

			if(target != null)
			{
				var enemy = before.OppBoard.Any(x => x.Id == target.Id) || before.OppHero?.Id == target.Id;
				e.TargetSide = enemy ? 1 : -1;
				var all = after.OppBoard.Concat(after.MyBoard).Concat(new[] { after.OppHero, after.MyHero }).Where(x => x != null);
				var now = all.FirstOrDefault(x => x.Id == target.Id);
				if(now == null)
				{
					e.TgtKilled = true;
					e.TgtChanged = true;
					e.TgtAtk = -target.Atk;
					e.TgtHp = -target.Hp;
				}
				else
				{
					e.TgtAtk = now.Atk - target.Atk;
					e.TgtHp = (now.Hp + now.Armor) - (target.Hp + target.Armor);
					e.TgtChanged = e.TgtAtk != 0 || e.TgtHp != 0 || now.CardId != target.CardId
					               || now.Taunt != target.Taunt || now.DivineShield != target.DivineShield
					               || now.Frozen != target.Frozen || now.Stealth != target.Stealth;
				}
			}
			return e;
		}

		/// <summary>把一次觀察併入課程（移動平均）。回傳這次是否「學到新東西」（第一次學會目標）。</summary>
		public static bool Learn(CardLesson l, Effect e, Weights w)
		{
			var firstTarget = l.Target < 0;
			l.Tries++;
			var a = 1.0 / Math.Min(l.Tries, 5); // 最近幾次權重較高（牌可能有隨機效果）
			l.Value += (e.Value(w) - l.Value) * a;
			l.Utc = DateTime.UtcNow.ToString("o");
			if(e.TargetSide != 0)
			{
				if(e.TargetSide > 0) l.EnemyTries++; else l.FriendlyTries++;
				if(!e.TgtChanged && !e.Visible)
				{
					l.NoEffect++;
				}
				else
				{
					l.TgtAtk += (e.TgtAtk - l.TgtAtk) * a;
					l.TgtHp += (e.TgtHp - l.TgtHp) * a;
					l.KillRate += ((e.TgtKilled ? 1 : 0) - l.KillRate) * a;
					// 目標變弱（扣血、死掉、降攻、被變形／沉默／冰凍）→ 這是「對敵人用」的牌；目標變強 → 對自己人用
					var worse = e.TgtKilled || e.TgtHp < 0 || e.TgtAtk < 0 || (e.TgtChanged && e.TgtHp <= 0 && e.TgtAtk <= 0);
					var better = !e.TgtKilled && (e.TgtHp > 0 || e.TgtAtk > 0);
					if(e.TargetSide > 0)
						l.Target = worse ? (int)TargetKind.EnemyMinion : better ? (int)TargetKind.FriendlyMinion : l.Target;
					else
						l.Target = better ? (int)TargetKind.FriendlyMinion : worse ? (int)TargetKind.EnemyMinion : l.Target;
				}
			}
			l.Summary = Describe(l);
			return firstTarget && l.Target >= 0;
		}

		public static string Describe(CardLesson l)
		{
			var parts = new List<string>();
			if(l.Target >= 0)
				parts.Add(l.Target == (int)TargetKind.EnemyMinion ? "對敵方手下用" : l.Target == (int)TargetKind.FriendlyMinion ? "對友方手下用" : ((TargetKind)l.Target).ToString());
			if(l.KillRate >= 0.5) parts.Add("會消滅目標");
			else
			{
				if(Math.Abs(l.TgtAtk) >= 0.5) parts.Add($"攻擊 {l.TgtAtk:+0;-0}");
				if(Math.Abs(l.TgtHp) >= 0.5) parts.Add($"血量 {l.TgtHp:+0;-0}");
			}
			parts.Add($"效果價值約 {l.Value:0.#}（觀察 {l.Tries} 次）");
			return string.Join("，", parts);
		}

		/// <summary>把學到的效果套到快照中的牌上（目標看不懂的牌改用學到的目標；用觀察到的價值估分）。</summary>
		public static void Apply(Snapshot s, IDictionary<string, CardLesson> lessons)
		{
			if(s == null || lessons == null || lessons.Count == 0)
				return;
			foreach(var c in s.Hand.Concat(new[] { s.MyHeroPower }).Where(x => x != null && !string.IsNullOrEmpty(x.CardId)))
			{
				if(!lessons.TryGetValue(c.CardId, out var l) || l.Tries == 0)
					continue;
				ApplyOne(c, l);
			}
		}

		public static void ApplyOne(Ent c, CardLesson l)
		{
			c.Learned = l.Value;
			if(c.Target == TargetKind.Unknown && l.Target >= 0)
			{
				c.Target = (TargetKind)l.Target;
				c.LearnedTarget = true;
				c.LAtk = (int)Math.Round(l.TgtAtk);
				c.LHp = (int)Math.Round(l.TgtHp);
				c.LKill = l.KillRate >= 0.5;
			}
		}

		/// <summary>還沒學會、值得在回合結束前「試出看看」的牌：目標看不懂，試過不到 3 次。</summary>
		public static bool WorthTrying(Ent c, IDictionary<string, CardLesson> lessons)
		{
			if(c == null || c.Target != TargetKind.Unknown || string.IsNullOrEmpty(c.CardId))
				return false;
			return !lessons.TryGetValue(c.CardId, out var l) || l.Tries + l.NoEffect < 3;
		}

		/// <summary>試出時選哪個目標：沒試過敵方 → 敵方最有價值的手下；敵方試了沒效果 → 改試我方最強手下。</summary>
		public static Ent TrialTarget(Snapshot s, CardLesson l)
		{
			var enemies = s.OppBoard.Where(m => m.Kind == Kind.Minion && !m.Stealth).OrderByDescending(m => m.Value).ToList();
			var friends = s.MyBoard.Where(m => m.Kind == Kind.Minion).OrderByDescending(m => m.Value).ToList();
			var triedEnemyNoEffect = l != null && l.EnemyTries > 0 && l.NoEffect >= l.EnemyTries;
			if(enemies.Count > 0 && !triedEnemyNoEffect)
				return enemies[0];
			if(friends.Count > 0)
				return friends[0];
			return enemies.FirstOrDefault();
		}
	}
}
