using System.Collections.Generic;
using System.Linq;

namespace HsHelper.Bot
{
	/// <summary>
	/// 手牌／牌庫能力統計（v0.9.5）：每抽一張、打出一張就重新整理「手上和牌庫裡還有哪些能力」，
	/// 顯示在面板、寫進 log，也提供 AI 估計「牌庫還有多少爆發」。
	/// </summary>
	public static class CardSummary
	{
		public class Stats
		{
			public int Cards, Damage, Removal, AoE, Heal, Buff, Summon, Draw, Taunt, Charge, Burn;
			public double BurnPerCard => Cards > 0 ? Burn / (double)Cards : 0; // 平均每抽一張能多打幾點臉
		}

		public static Stats Count(IEnumerable<Ent> cards)
		{
			var s = new Stats();
			foreach(var c in cards ?? Enumerable.Empty<Ent>())
			{
				if(c == null || c.IsCoin) continue;
				s.Cards++;
				var tags = CardText.Tags(c);
				bool Has(string p) => tags.Any(t => t.StartsWith(p) || t.Contains(p));
				if(c.AoE) s.AoE++;
				else if(c.Damage > 0 && c.Target != TargetKind.None) s.Damage++;
				if(Has("消滅") || Has("變形") || Has("沉默") || Has("冰凍") || Has("彈回")) s.Removal++;
				if(c.Heal > 0) s.Heal++;
				if(Has("增益") || c.BuffAllA > 0 || c.BuffAllH > 0) s.Buff++;
				if(c.SumN > 0) s.Summon++;
				if(c.Draw > 0 || c.Gen > 0) s.Draw++;
				if(c.Kind == Kind.Minion && c.Taunt) s.Taunt++;
				if(c.Kind == Kind.Minion && (c.Charge || c.Rush)) s.Charge++;
				// 爆發：直接打臉的傷害、可打英雄的傷害法術、衝鋒手下的攻擊、英雄加攻
				var burn = c.FaceDmg + (c.Target == TargetKind.EnemyAny ? c.Damage : 0) + (c.Kind == Kind.Minion && c.Charge ? c.Atk : 0) + c.HeroAtk;
				s.Burn += burn;
			}
			return s;
		}

		public static string Describe(Stats s)
		{
			var p = new List<string>();
			void A(string name, int n) { if(n > 0) p.Add($"{name}×{n}"); }
			A("傷害", s.Damage); A("解場", s.Removal); A("全場", s.AoE); A("治療", s.Heal); A("增益", s.Buff);
			A("召喚", s.Summon); A("抽牌", s.Draw); A("嘲諷", s.Taunt); A("衝鋒突襲", s.Charge);
			if(s.Burn > 0) p.Add($"爆發{s.Burn}");
			return p.Count == 0 ? "—" : string.Join(" ", p);
		}

		/// <summary>面板一行：「手牌 5：傷害×2 …｜牌庫 18：全場×1 …」。</summary>
		public static string Line(Snapshot snap)
		{
			var h = Count(snap.Hand);
			var line = $"手牌 {h.Cards}：{Describe(h)}";
			if(snap.Deck != null && snap.Deck.Count > 0)
				line += $"｜牌庫 {snap.Deck.Count}：{Describe(Count(snap.Deck))}";
			return line;
		}
	}
}
