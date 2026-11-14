using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HsHelper.Bot;

namespace HsHelper.Tests
{
	/// <summary>
	/// 決策 AI 模擬測試。
	///  A. 情境測試：特定局面下 AI 是否做出正確判斷（斬殺、解嘲諷、交換、用滿法力…）
	///  B. 隨機局面壓力測試：數百個隨機局面不出錯、動作合法、思考時間夠短
	///  C. 模擬對戰：在簡化版爐石引擎中，AI 對上「只會打臉」的對手，統計勝率
	/// 任何一項失敗 → 程式回傳非 0，GitHub Actions 就不會產出安裝檔。
	/// </summary>
	public static class Program
	{
		private static int _pass, _fail;
		private static readonly List<string> Report = new List<string>();

		public static int Main()
		{
			Section("A. 情境測試");
			Scenarios();
			Section("B. 隨機局面壓力測試");
			Fuzz(400);
			Section("C. 模擬對戰（AI vs 只會打臉的對手）");
			SelfPlay(200);

			var summary = $"結果：{_pass} 通過，{_fail} 失敗";
			Console.WriteLine();
			Console.WriteLine(summary);
			Report.Add("");
			Report.Add($"**{summary}**");
			var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
			if(!string.IsNullOrEmpty(stepSummary))
				File.AppendAllLines(stepSummary, new[] { "## 爐石輔助：決策 AI 模擬測試", "" }.Concat(Report));
			return _fail == 0 ? 0 : 1;
		}

		private static void Section(string title)
		{
			Console.WriteLine();
			Console.WriteLine("== " + title + " ==");
			Report.Add("");
			Report.Add("### " + title);
		}

		private static void Check(string name, bool ok, string detail = "")
		{
			if(ok) _pass++; else _fail++;
			var line = $"{(ok ? "✅" : "❌")} {name}{(string.IsNullOrEmpty(detail) ? "" : "：" + detail)}";
			Console.WriteLine(line);
			Report.Add("- " + line);
		}

		// ───────── 建立測試用局面 ─────────
		private static int _id = 100;

		private static Ent Minion(int atk, int hp, string name = null, bool taunt = false, bool ds = false,
			bool canAttack = true, bool poison = false, bool rushJustPlayed = false)
			=> new Ent
			{
				Id = _id++, Name = name ?? $"{atk}/{hp}", CardId = $"M{atk}_{hp}", Kind = Kind.Minion,
				Atk = atk, Hp = hp, Taunt = taunt, DivineShield = ds, Poisonous = poison,
				Exhausted = !canAttack || rushJustPlayed, Rush = rushJustPlayed, TurnsInPlay = rushJustPlayed ? 0 : 1, Text = ""
			};

		private static Ent Card(int cost, int atk, int hp, string name = null, bool charge = false, bool taunt = false)
			=> new Ent
			{
				Id = _id++, Name = name ?? $"{cost}費{atk}/{hp}", CardId = $"C{cost}_{atk}_{hp}{(charge ? "c" : "")}{(taunt ? "t" : "")}",
				Kind = Kind.Minion, Cost = cost, Atk = atk, Hp = hp, Charge = charge, Taunt = taunt, Text = ""
			};

		private static Ent Spell(int cost, string text, string name)
		{
			var e = new Ent { Id = _id++, Name = name, CardId = "S" + name, Kind = Kind.Spell, Cost = cost, Text = text };
			CardText.Classify(e);
			return e;
		}

		private static Ent Coin() => new Ent { Id = _id++, Name = "幸運幣", CardId = "COIN", Kind = Kind.Spell, Cost = 0, IsCoin = true, Text = "" };

		private static Ent Hero(int hp, int armor = 0) =>
			new Ent { Id = _id++, Name = "英雄", Kind = Kind.Hero, Hp = hp, Armor = armor, Text = "" };

		private static Ent HeroPower(string text, int cost = 2)
		{
			var e = new Ent { Id = _id++, Name = "英雄技能", CardId = "HP", Kind = Kind.HeroPower, Cost = cost, Text = text };
			CardText.Classify(e, true);
			return e;
		}

		private static Snapshot Board(int mana, Ent[] mine, Ent[] theirs, Ent[] hand = null, int myHp = 30, int oppHp = 30, Ent heroPower = null)
			=> new Snapshot
			{
				InGame = true, MyTurn = true, MainAction = true, Mana = mana,
				MyHero = Hero(myHp), OppHero = Hero(oppHp),
				MyBoard = mine.ToList(), OppBoard = theirs.ToList(),
				Hand = (hand ?? new Ent[0]).ToList(), MyHeroPower = heroPower,
				HasCoin = hand?.Any(c => c.IsCoin) ?? false
			};

		private static BotAction Decide(Snapshot s, out string plan) => Search.BestFirstAction(s, new HashSet<int>(), out plan);

		// ───────── A. 情境測試 ─────────
		private static void Scenarios()
		{
			// 1. 斬殺
			{
				var a = Minion(3, 3); var b = Minion(3, 3);
				var s = Board(0, new[] { a, b }, new[] { Minion(2, 2) }, oppHp: 6);
				var act = Decide(s, out var plan);
				Check("看到斬殺會打臉", act?.Type == ActionType.Attack && act.Target?.Id == s.OppHero.Id, plan);
			}
			// 2. 嘲諷要先解
			{
				var taunt = Minion(1, 3, "嘲諷手下", taunt: true);
				var s = Board(0, new[] { Minion(4, 4) }, new[] { taunt }, oppHp: 30);
				var act = Decide(s, out var plan);
				Check("有嘲諷先攻擊嘲諷", act?.Type == ActionType.Attack && act.Target?.Id == taunt.Id, plan);
			}
			// 3. 划算交換：3/5 打 4/2（殺掉又活著）
			{
				var threat = Minion(4, 2, "4/2威脅");
				var s = Board(0, new[] { Minion(3, 5) }, new[] { threat }, oppHp: 30);
				var act = Decide(s, out var plan);
				Check("划算交換：解掉高攻擊手下", act?.Target?.Id == threat.Id, plan);
			}
			// 4. 法力用滿：5 法力，手上 2 費＋3 費＋4 費 → 應打 2＋3
			{
				var c2 = Card(2, 2, 3); var c3 = Card(3, 3, 4); var c4 = Card(4, 4, 5);
				var s = Board(5, new Ent[0], new Ent[0], new[] { c2, c3, c4 });
				Decide(s, out var plan);
				Check("法力用滿（5 費出 2＋3）", plan.Contains(c2.Name) && plan.Contains(c3.Name) && !plan.Contains(c4.Name), plan);
			}
			// 5. 幸運幣：1 法力＋幸運幣，手上 2 費 → 用幣跳費
			{
				var coin = Coin(); var c2 = Card(2, 3, 2);
				var s = Board(1, new Ent[0], new Ent[0], new[] { coin, c2 });
				var act = Decide(s, out var plan);
				Check("用幸運幣跳費出 2 費", act?.Source?.Id == coin.Id && plan.Contains(c2.Name), plan);
			}
			// 6. 不亂用幸運幣：2 法力，手上只有 2 費
			{
				var coin = Coin(); var c2 = Card(2, 3, 2);
				var s = Board(2, new Ent[0], new Ent[0], new[] { coin, c2 });
				var act = Decide(s, out var plan);
				Check("不浪費幸運幣", act?.Source?.Id == c2.Id && !plan.Contains("幸運幣"), plan);
			}
			// 7. 危險時優先保命：我 5 血，對手場上 3/1、3/1，我有 2/2、2/2
			{
				var e1 = Minion(3, 1); var e2 = Minion(3, 1);
				var s = Board(0, new[] { Minion(2, 2), Minion(2, 2) }, new[] { e1, e2 }, myHp: 5, oppHp: 20);
				Decide(s, out var plan);
				var firstKill = plan.Split('→')[0];
				Check("血量危險時先解威脅", firstKill.Contains("攻擊 3/1"), plan);
			}
			// 8. 聖盾：先用小手下破盾，再用大的殺
			{
				var dsTaunt = Minion(3, 3, "聖盾嘲諷", taunt: true, ds: true);
				var small = Minion(1, 3, "1/3"); var big = Minion(4, 4, "4/4");
				var s = Board(0, new[] { small, big }, new[] { dsTaunt }, oppHp: 30);
				var act = Decide(s, out var plan);
				Check("先用小手下破聖盾", act?.Source?.Id == small.Id && act.Target?.Id == dsTaunt.Id, plan);
			}
			// 9. 傷害法術打敵方手下
			{
				var bolt = Spell(1, "Deal 3 damage to a minion.", "3傷法術");
				var target = Minion(5, 3, "5/3");
				var s = Board(1, new Ent[0], new[] { target }, new[] { bolt });
				var act = Decide(s, out var plan);
				Check("傷害法術解掉 5/3", act?.Source?.Id == bolt.Id && act.Target?.Id == target.Id, plan);
			}
			// 10. 傷害法術斬殺英雄
			{
				var fireball = Spell(4, "Deal 6 damage.", "6傷法術");
				var s = Board(4, new Ent[0], new[] { Minion(2, 2) }, new[] { fireball }, oppHp: 6);
				var act = Decide(s, out var plan);
				Check("法術直接斬殺英雄", act?.Target?.Id == s.OppHero.Id, plan);
			}
			// 11. 全場 AOE：對手場上很多小怪才用
			{
				var aoe = Spell(4, "Deal 2 damage to all enemy minions.", "全場2傷");
				var s = Board(4, new[] { Minion(3, 3) }, new[] { Minion(2, 2), Minion(2, 1), Minion(3, 2), Minion(1, 2) }, new[] { aoe });
				Decide(s, out var plan);
				Check("對手鋪場時用 AOE", plan.Contains("全場2傷"), plan);
			}
			// 12. 突襲手下剛上場不能打臉
			{
				var rush = Minion(3, 3, "突襲", rushJustPlayed: true);
				var s = Board(0, new[] { rush }, new Ent[0], oppHp: 3);
				var act = Decide(s, out var plan);
				Check("突襲剛上場不打臉", act == null || act.Target?.Id != s.OppHero.Id, plan);
			}
			// 13. 衝鋒手下打出來馬上斬殺
			{
				var charger = Card(3, 4, 2, "衝鋒4/2", charge: true);
				var s = Board(3, new Ent[0], new Ent[0], new[] { charger }, oppHp: 4);
				Decide(s, out var plan);
				Check("打出衝鋒手下斬殺", plan.Contains("衝鋒4/2") && plan.Contains("攻擊 英雄"), plan);
			}
			// 14. 英雄技能（獵人：對敵方英雄 2 傷）有剩法力就用
			{
				var hp = HeroPower("Deal 2 damage to the enemy hero.");
				var s = Board(2, new Ent[0], new Ent[0], heroPower: hp, oppHp: 30);
				var act = Decide(s, out var plan);
				Check("剩餘法力使用英雄技能", act?.Type == ActionType.HeroPower, plan);
			}
			// 15. 不要把好手下送死（2/2 撞 5/5）
			{
				var s = Board(0, new[] { Minion(2, 2) }, new[] { Minion(5, 5) }, oppHp: 30);
				Decide(s, out var plan);
				Check("不亂送手下", !plan.Contains("攻擊 5/5"), plan);
			}
			// 16. 換牌規則
			{
				var hand = new List<Ent> { Card(1, 1, 2), Card(4, 4, 5), Card(6, 6, 6) };
				Check("換牌：先手換掉 4 費、6 費",
					!Planner.ShouldReplace(hand[0], hand, false) && Planner.ShouldReplace(hand[1], hand, false) && Planner.ShouldReplace(hand[2], hand, false));
				Check("換牌：後手有低費牌時留一張 4 費", !Planner.ShouldReplace(hand[1], hand, true));
			}
			// 17. 卡牌敘述解析
			{
				Check("解析「Deal 3 damage to a minion」→ 敵方手下", Spell(1, "Deal $3 damage to a minion.", "x").Target == TargetKind.EnemyMinion);
				Check("解析「Give a friendly minion +2/+2」→ 友方手下", Spell(1, "Give a friendly minion +2/+2.", "y").Target == TargetKind.FriendlyMinion);
				Check("解析「Draw 2 cards」→ 不需目標", Spell(1, "Draw 2 cards.", "z").Target == TargetKind.None);
				Check("解析法師英雄技能 → 需要目標", HeroPower("Deal $1 damage.").Target == TargetKind.EnemyAny);
				Check("解析火球術「Deal 6 damage」→ 敵方任意目標", Spell(4, "Deal $6 damage.", "f").Target == TargetKind.EnemyAny);
				Check("解析「Deal 2 damage to all minions」→ AOE 不需目標", Spell(4, "Deal 2 damage to all minions.", "g") is var g && g.Target == TargetKind.None && g.AoE);
				Check("解析獵人英雄技能 → 不需目標", HeroPower("Deal 2 damage to the enemy hero.").Target == TargetKind.None);
			}
		}

		// ───────── B. 隨機局面 ─────────
		private static void Fuzz(int count)
		{
			var rng = new Random(12345);
			int illegal = 0, errors = 0;
			long maxMs = 0, totalMs = 0;
			for(var i = 0; i < count; i++)
			{
				var s = RandomBoard(rng);
				var sw = Stopwatch.StartNew();
				try
				{
					var act = Decide(s, out _);
					sw.Stop();
					if(act != null && !IsLegal(s, act, out _))
						illegal++;
				}
				catch(Exception ex)
				{
					errors++;
					if(errors <= 3) Console.WriteLine(ex);
				}
				maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds);
				totalMs += sw.ElapsedMilliseconds;
			}
			Check($"{count} 個隨機局面沒有出錯", errors == 0, $"{errors} 個錯誤");
			Check("所有動作都合法", illegal == 0, $"{illegal} 個不合法");
			Check("思考時間：最長 < 4 秒", maxMs < 4000, $"平均 {totalMs / count} ms，最長 {maxMs} ms");
		}

		private static Snapshot RandomBoard(Random r)
		{
			Ent RM(bool mine) => Minion(r.Next(0, 8), r.Next(1, 9), taunt: r.Next(5) == 0, ds: r.Next(8) == 0,
				canAttack: !mine || r.Next(4) != 0, poison: r.Next(12) == 0);
			var mine = Enumerable.Range(0, r.Next(0, 8)).Select(_ => RM(true)).ToArray();
			var theirs = Enumerable.Range(0, r.Next(0, 8)).Select(_ => RM(false)).ToArray();
			var hand = Enumerable.Range(0, r.Next(0, 11)).Select(_ =>
			{
				var k = r.Next(10);
				if(k == 0) return Spell(r.Next(1, 5), "Deal 3 damage.", "火");
				if(k == 1) return Spell(r.Next(2, 6), "Deal 2 damage to all minions.", "AOE");
				if(k == 2) return Spell(r.Next(1, 4), "Give a friendly minion +2/+2.", "BUFF");
				if(k == 3) return Spell(r.Next(1, 4), "Draw 2 cards.", "抽");
				var c = r.Next(0, 10);
				return Card(c, Math.Max(1, c + r.Next(-1, 2)), Math.Max(1, c + r.Next(-1, 3)), charge: r.Next(10) == 0, taunt: r.Next(6) == 0);
			}).ToArray();
			return Board(r.Next(0, 11), mine, theirs, hand, myHp: r.Next(1, 31), oppHp: r.Next(1, 31),
				heroPower: r.Next(2) == 0 ? HeroPower("Deal 2 damage to the enemy hero.") : null);
		}

		private static bool IsLegal(Snapshot s, BotAction a, out string why)
		{
			why = "";
			switch(a.Type)
			{
				case ActionType.Play:
					if(!s.Hand.Contains(a.Source)) { why = "手上沒有這張"; return false; }
					if(a.Source.Cost > s.Mana) { why = "法力不足"; return false; }
					if(a.Source.Kind == Kind.Minion && s.MyBoard.Count >= 7) { why = "板面已滿"; return false; }
					return true;
				case ActionType.HeroPower:
					return s.MyHeroPower != null && s.MyHeroPower.Cost <= s.Mana;
				case ActionType.Attack:
					var att = a.Source;
					if(att.Atk <= 0) { why = "攻擊力 0"; return false; }
					if(att.Kind == Kind.Minion && att.Exhausted && !att.Charge && !att.Rush) { why = "手下不能攻擊"; return false; }
					var taunts = s.OppBoard.Where(m => m.Taunt && !m.Stealth).ToList();
					if(taunts.Count > 0 && !taunts.Contains(a.Target)) { why = "沒先打嘲諷"; return false; }
					if(a.Target == s.OppHero && att.Rush && att.Exhausted) { why = "突襲打臉"; return false; }
					return true;
				default:
					return true;
			}
		}

		// ───────── C. 模擬對戰 ─────────
		private class Side
		{
			public int Hp = 30, MaxMana, Mana, Fatigue;
			public bool HeroPowerUsed;
			public List<Ent> Deck = new List<Ent>(), Hand = new List<Ent>(), Board = new List<Ent>();
			public Ent Hero, Power;
		}

		private static List<Ent> RandomDeck(Random r)
		{
			var deck = new List<Ent>();
			for(var i = 0; i < 30; i++)
			{
				var k = r.Next(10);
				if(k == 0) { deck.Add(Spell(r.Next(1, 4), "Deal 3 damage.", "火球")); continue; }
				if(k == 1) { deck.Add(Spell(4, "Deal 2 damage to all enemy minions.", "爆炸")); continue; }
				var c = new[] { 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 7 }[r.Next(14)];
				deck.Add(Card(c, Math.Max(1, c + r.Next(-1, 2)), Math.Max(1, c + r.Next(0, 2)), charge: r.Next(12) == 0, taunt: r.Next(5) == 0));
			}
			return deck;
		}

		private static void Draw(Side s)
		{
			if(s.Deck.Count == 0) { s.Hp -= ++s.Fatigue; return; }
			var c = s.Deck[0];
			s.Deck.RemoveAt(0);
			if(s.Hand.Count < 10) s.Hand.Add(c);
		}

		private static void SelfPlay(int games)
		{
			var rng = new Random(2026);
			int wins = 0, illegal = 0, draws = 0;
			for(var g = 0; g < games; g++)
			{
				var ai = new Side { Hero = Hero(30), Power = HeroPower("Deal 2 damage to the enemy hero.") };
				var bot = new Side { Hero = Hero(30), Power = HeroPower("Deal 2 damage to the enemy hero.") };
				ai.Deck = RandomDeck(rng);
				bot.Deck = RandomDeck(rng); // 雙方都是隨機牌組，公平比較
				var aiFirst = g % 2 == 0;
				for(var i = 0; i < 3; i++) { Draw(ai); Draw(bot); }
				Draw(aiFirst ? bot : ai);
				(aiFirst ? bot : ai).Hand.Add(Coin());

				var turn = 0;
				var aiTurn = aiFirst;
				while(ai.Hp > 0 && bot.Hp > 0 && turn < 80)
				{
					var me = aiTurn ? ai : bot;
					var op = aiTurn ? bot : ai;
					me.MaxMana = Math.Min(10, me.MaxMana + 1);
					me.Mana = me.MaxMana;
					me.HeroPowerUsed = false;
					foreach(var m in me.Board) { m.Exhausted = false; m.AttacksThisTurn = 0; m.TurnsInPlay++; }
					Draw(me);
					if(aiTurn) illegal += AiTurn(me, op);
					else FaceBotTurn(me, op);
					aiTurn = !aiTurn;
					turn++;
				}
				if(ai.Hp > 0 && bot.Hp <= 0) wins++;
				else if(!(ai.Hp <= 0 && bot.Hp > 0)) draws++;
			}
			var rate = wins * 100.0 / games;
			Check($"模擬 {games} 場對戰勝率 ≥ 65%", rate >= 65, $"{wins} 勝 {games - wins - draws} 敗 {draws} 平（勝率 {rate:F0}%）");
			Check("對戰中所有動作都合法", illegal == 0, $"{illegal} 個不合法");
		}

		private static Snapshot FromSides(Side me, Side op)
		{
			me.Hero.Hp = me.Hp; op.Hero.Hp = op.Hp;
			me.Power.Exhausted = me.HeroPowerUsed;
			return new Snapshot
			{
				InGame = true, MyTurn = true, MainAction = true, Mana = me.Mana,
				MyHero = me.Hero, OppHero = op.Hero, MyHeroPower = me.HeroPowerUsed ? null : me.Power,
				MyBoard = me.Board.ToList(), OppBoard = op.Board.ToList(), Hand = me.Hand.ToList(),
				HasCoin = me.Hand.Any(c => c.IsCoin)
			};
		}

		private static int AiTurn(Side me, Side op)
		{
			var bad = 0;
			var skip = new HashSet<int>();
			for(var step = 0; step < 40 && me.Hp > 0 && op.Hp > 0; step++)
			{
				var s = FromSides(me, op);
				var act = Search.BestFirstAction(s, skip, out _);
				if(act == null || act.Type == ActionType.EndTurn)
					break;
				if(!IsLegal(s, act, out _)) { bad++; skip.Add(act.Source.Id); continue; }
				Apply(me, op, act);
			}
			return bad;
		}

		/// <summary>對手：照費用高到低出牌，能打臉就打臉（只在有嘲諷時才打手下）。</summary>
		private static void FaceBotTurn(Side me, Side op)
		{
			if(me.Hand.Any(c => c.IsCoin) && me.Hand.Any(c => c.Cost == me.Mana + 1))
				Apply(me, op, new BotAction { Type = ActionType.Play, Source = me.Hand.First(c => c.IsCoin) });
			foreach(var c in me.Hand.Where(c => !c.IsCoin).OrderByDescending(c => c.Cost).ToList())
			{
				if(c.Cost > me.Mana || (c.Kind == Kind.Minion && me.Board.Count >= 7)) continue;
				Ent target = null;
				if(c.Kind == Kind.Spell && c.Target != TargetKind.None)
					target = op.Board.OrderByDescending(m => m.Atk).FirstOrDefault() ?? op.Hero;
				Apply(me, op, new BotAction { Type = ActionType.Play, Source = c, Target = target });
			}
			if(!me.HeroPowerUsed && me.Mana >= 2)
				Apply(me, op, new BotAction { Type = ActionType.HeroPower, Source = me.Power });
			foreach(var m in me.Board.ToList())
			{
				if(m.Exhausted && !m.Charge || m.Atk <= 0 || m.AttacksThisTurn > 0 || !me.Board.Contains(m)) continue;
				var taunt = op.Board.FirstOrDefault(x => x.Taunt);
				Apply(me, op, new BotAction { Type = ActionType.Attack, Source = m, Target = taunt ?? op.Hero });
				if(op.Hp <= 0) return;
			}
		}

		private static void Damage(Side side, Ent target, int dmg)
		{
			if(target == side.Hero) { side.Hp -= dmg; return; }
			if(target.DivineShield && dmg > 0) { target.DivineShield = false; return; }
			target.Hp -= dmg;
		}

		private static void Apply(Side me, Side op, BotAction a)
		{
			switch(a.Type)
			{
				case ActionType.Play:
				{
					var c = a.Source;
					me.Hand.Remove(c);
					me.Mana -= c.Cost;
					if(c.IsCoin) { me.Mana++; break; }
					if(c.Kind == Kind.Minion)
					{
						c.Exhausted = !c.Charge;
						c.TurnsInPlay = 0;
						c.AttacksThisTurn = 0;
						me.Board.Add(c);
					}
					else if(c.Kind == Kind.Spell)
					{
						if(c.AoE)
						{
							foreach(var m in op.Board.ToList()) Damage(op, m, c.Damage);
							if(!c.Text.Contains("enemy")) foreach(var m in me.Board.ToList()) Damage(me, m, c.Damage);
						}
						else if(a.Target != null)
						{
							var side = op.Board.Contains(a.Target) || a.Target == op.Hero ? op : me;
							Damage(side, a.Target, c.Damage);
						}
					}
					break;
				}
				case ActionType.HeroPower:
					me.Mana -= me.Power.Cost;
					me.HeroPowerUsed = true;
					op.Hp -= 2;
					break;
				case ActionType.Attack:
				{
					var att = a.Source;
					var def = a.Target;
					att.AttacksThisTurn++;
					att.Exhausted = true;
					if(def == op.Hero) op.Hp -= att.Atk;
					else
					{
						var defAtk = def.Atk;
						Damage(op, def, att.Atk);
						Damage(me, att, defAtk);
					}
					break;
				}
			}
			me.Board.RemoveAll(m => m.Hp <= 0);
			op.Board.RemoveAll(m => m.Hp <= 0);
		}
	}
}
