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

		public static int Main(string[] args)
		{
			if(args.Length > 0 && args[0] == "--tune")
			{
				Tuner.Run(args.Length > 1 ? int.Parse(args[1]) : 30);
				return 0;
			}
			if(args.Length > 1 && args[0] == "--scan")
			{
				CardScan.Run(args[1]);
				return 0;
			}
			if(args.Length > 0 && args[0] == "--exp")
			{
				LearningTests.Experiment(args.Length > 1 ? int.Parse(args[1]) : 300);
				return 0;
			}
			if(args.Length > 1 && args[0] == "--seed")
			{
				LearningTests.GenerateSeed(args[1], args.Length > 2 ? int.Parse(args[2]) : 80);
				return 0;
			}
			var seedPath = Environment.GetEnvironmentVariable("HSHELPER_SEED") ?? "../../src/HsHelper/Resources/learning-seed.json";
			Section("A. 情境測試");
			Scenarios();
			Section("B. 隨機局面壓力測試");
			Fuzz(400);
			Section("C. 模擬對戰");
			SelfPlay(200);
			Section("D. 學習功能（匯入匯出、從對局學習）");
			LearningTests.Run(seedPath);
			Section("E. 新機制報告");
			ReportTests();
			Section("F. 動作成功判斷（v0.7.0 實測報告：13 張牌被誤判打不出來）");
			VerifyTests();

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

		public static void Check(string name, bool ok, string detail = "")
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
			var kind = text.IndexOf("Battlecry", StringComparison.OrdinalIgnoreCase) >= 0 ? Kind.Minion : Kind.Spell;
			var e = new Ent { Id = _id++, Name = name, CardId = "S" + name, Kind = kind, Cost = cost, Text = text };
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
			// 12b. 實測報告：突襲手下沒有疲勞標記，仍不能打臉
			{
				var rush = Minion(3, 1, "紅鰓刃顎魚人", canAttack: true);
				rush.Rush = true; rush.TurnsInPlay = 0; rush.Exhausted = false;
				var s = Board(0, new[] { rush }, new Ent[0], oppHp: 3);
				var act = Decide(s, out var plan);
				Check("突襲手下（沒有疲勞標記）剛上場不打臉", act == null || act.Target?.Id != s.OppHero.Id, string.IsNullOrEmpty(plan) ? "（結束回合）" : plan);
				var e = Minion(4, 2, "4/2威脅");
				var s2 = Board(0, new[] { rush }, new[] { e }, oppHp: 30);
				var act2 = Decide(s2, out var plan2);
				Check("突襲手下剛上場可以去解敵方手下", act2?.Target?.Id == e.Id, plan2);
			}
			// 12c. 實測報告：冰川裂片「冰凍一個敵人」選目標，不能放棄
			{
				var shard = Spell(1, "<b>Battlecry:</b> <b>Freeze</b> an enemy.", "冰川裂片");
				shard.Kind = Kind.Minion;
				var big = Minion(5, 5, "5/5"); var small = Minion(1, 1, "1/1");
				var s = Board(1, new Ent[0], new[] { small, big }, new[] { shard });
				Check("冰凍戰吼：凍攻擊力最高的敵方手下", Planner.PickAnyTarget(s, shard, shard.Target)?.Id == big.Id);
				var s2 = Board(1, new Ent[0], new Ent[0], new[] { shard });
				Check("冰凍戰吼：沒有敵方手下就凍英雄（不取消）", Planner.PickAnyTarget(s2, shard, shard.Target)?.Id == s2.OppHero.Id);
				var silencer = Spell(4, "<b>Battlecry:</b> <b>Silence</b> a minion.", "皇家圖書管理員");
				silencer.Kind = Kind.Minion;
				var s3 = Board(4, new Ent[0], new[] { small }, new[] { silencer });
				Check("消音戰吼：沒有好目標也要選一個合法目標", Planner.PickAnyTarget(s3, silencer, silencer.Target) != null);
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
			// 16a. 實測：0 費法術一直沒出
			{
				var free = Spell(0, "Gain 1 Mana Crystal this turn only.", "0費法術");
				var s = Board(0, new Ent[0], new Ent[0], new[] { free });
				var act = Decide(s, out var plan);
				Check("0 費法術會打出（沒水晶時也一樣）", act?.Source?.Id == free.Id, string.IsNullOrEmpty(plan) ? "（結束回合）" : plan);
				var wisp = Card(0, 1, 1, "0費1/1");
				var s2 = Board(0, new Ent[0], new Ent[0], new[] { wisp });
				Check("0 費手下會打出", Decide(s2, out var p2)?.Source?.Id == wisp.Id, p2);
			}
			// 16b. 需要目標的法術：場上沒有合法目標就不出（實測報告：沒敵人還選了要目標的牌）
			{
				var silence = Spell(1, "<b>Silence</b> a minion.", "沉默術");
				var bolt = Spell(3, "Deal $3 damage to a minion.", "火焰箭");
				var s = Board(4, new Ent[0], new Ent[0], new[] { silence, bolt }, oppHp: 30);
				var act = Decide(s, out var plan);
				Check("場上沒手下：不出只能選手下的法術", !plan.Contains("沉默術") && !plan.Contains("火焰箭"), string.IsNullOrEmpty(plan) ? "（結束回合）" : plan);
				var s2 = Board(4, new Ent[0], new[] { Minion(4, 3, "4/3") }, new[] { bolt }, oppHp: 30);
				Decide(s2, out var plan2);
				Check("有手下時才用它解場", plan2.Contains("火焰箭"), plan2);
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
				// 實測報告：靈魂虹吸被選到自己英雄
				Check("解析靈魂虹吸「Destroy a minion. Restore 3 Health to your hero.」→ 敵方手下",
					Spell(6, "Destroy a minion. Restore #3 Health to_your hero.", "k").Target == TargetKind.EnemyMinion);
				Check("解析巫毒巫醫「Restore 2 Health」→ 友方（不會補敵方英雄）", Spell(1, "<b>Battlecry:</b> Restore #2_Health.", "l").Target == TargetKind.FriendlyAny);
				Check("解析神聖新星（全場傷害＋全場治療）→ 不需目標", Spell(3, "Deal $2 damage to all enemy minions. Restore #2 Health to all friendly characters.", "m").Target == TargetKind.None);
				Check("解析吸取生命「Lifesteal. Deal 2 damage.」→ 敵方任意", Spell(3, "<b>Lifesteal</b>. Deal $2 damage.", "n").Target == TargetKind.EnemyAny);
				Check("解析秘法飛彈（隨機）→ 不需目標", Spell(1, "Deal $3 damage randomly split among all enemies.", "o").Target == TargetKind.None);
				Check("解析生命分流「Draw a card and take 2 damage」→ 不需目標", HeroPower("<b>Hero Power</b> Draw a card and take $2 damage.").Target == TargetKind.None);
				Check("解析「Set a minion's Attack and Health to 1」→ 不是治療", Spell(1, "Set a minion's Attack and Health to 1.", "p").Target == TargetKind.EnemyMinion);
				Check("解析「Draw a minion」→ 不需目標", Spell(1, "Draw a minion. Restore Health to your hero equal to its Cost.", "q").Target == TargetKind.None);
				Check("解析「After you play a minion, draw a card」→ 不需目標", Spell(2, "After you play a minion, draw a card.", "r").Target == TargetKind.None);
				{
					var pact = Spell(1, "Destroy a friendly minion. Restore #8 Health to your hero.", "黑暗契約");
					var weak = Minion(1, 1, "1/1"); var strong = Minion(6, 6, "6/6");
					var sp = Board(1, new[] { strong, weak }, new Ent[0], new[] { pact });
					Check("「消滅一個友方手下」挑最弱的", pact.Target == TargetKind.FriendlyMinion && Planner.PickTarget(sp, pact, pact.Target)?.Id == weak.Id);
				}
				Check("解析冰川裂片「Freeze an enemy」→ 需要目標（可選英雄）", Spell(1, "<b>Battlecry:</b> <b>Freeze</b> an enemy.", "h").Target == TargetKind.EnemyAny);
				Check("解析「Silence a minion」→ 只能選手下", Spell(1, "<b>Silence</b> a minion.", "i").Target == TargetKind.EnemyMinion);
				Check("解析「Deal 3 damage to a minion」→ 只能選手下", Spell(3, "<b>Lifesteal</b> Deal $3 damage to a minion. If it dies, return this to your hand.", "j").Target == TargetKind.EnemyMinion);
			}
		}

		// ───────── F. 動作成功判斷 ─────────
		private static void VerifyTests()
		{
			var croc = Card(2, 2, 3, "淡水鱷");
			var other = Card(1, 1, 1);
			var before = Board(4, new Ent[0], new Ent[0], new[] { croc, other });

			// 實測情況：牌已經在場上，但 HDT 的手牌資料還沒更新
			var lagging = Board(4, new[] { croc }, new Ent[0], new[] { croc, other });
			Check("牌已在場上、手牌資料還沒更新 → 算成功", Verify.PlayResolved(before, lagging, croc));

			var manaSpent = Board(2, new Ent[0], new Ent[0], new[] { croc, other });
			Check("法力已扣 → 算成功", Verify.PlayResolved(before, manaSpent, croc));

			var left = Board(2, new[] { croc }, new Ent[0], new[] { other });
			Check("牌離開手牌 → 算成功", Verify.PlayResolved(before, left, croc));

			var nothing = Board(4, new Ent[0], new Ent[0], new[] { croc, other });
			Check("什麼都沒變 → 算失敗（才會取消並回報）", !Verify.PlayResolved(before, nothing, croc));

			var otherPlayed = Board(3, new[] { other }, new Ent[0], new[] { croc });
			Check("扣的是別張牌的法力（不夠扣這張）→ 不誤判", !Verify.PlayResolved(before, otherPlayed, croc));

			var croc2 = Card(2, 2, 3, "淡水鱷");
			croc2.CardId = croc.CardId;
			var twoCrocs = Board(4, new Ent[0], new Ent[0], new[] { croc, croc2 });
			var otherCopyPlayed = Board(4, new Ent[0], new Ent[0], new[] { croc });
			Check("拖到另一張同名牌也算成功", Verify.PlayResolved(twoCrocs, otherCopyPlayed, croc2));

			var hp = HeroPower("Deal 2 damage to the enemy hero.");
			var hpBefore = Board(2, new Ent[0], new Ent[0], heroPower: hp);
			var hpAfter = Board(0, new Ent[0], new Ent[0], heroPower: hp);
			Check("英雄技能：法力已扣 → 算成功", Verify.HeroPowerResolved(hpBefore, hpAfter));
			Check("英雄技能：沒變化 → 算失敗", !Verify.HeroPowerResolved(hpBefore, Board(2, new Ent[0], new Ent[0], heroPower: hp)));
		}

		// ───────── E. 新機制報告 ─────────
		private static void ReportTests()
		{
			var r = new MechReport();
			var kw = r.Touch(MechKind.Keyword, "starship", "1.60.1", "2026-11-15 20:00");
			MechReport.AddExample(kw, new CardExample { CardId = "GDB_001", NameZh = "測試星艦", NameEn = "Test | Ship", TextEn = "<b>Starship</b> piece.", Tags = "STARSHIP=1" });
			MechReport.AddExample(kw, new CardExample { CardId = "GDB_001" }); // 重複不加
			var stuck = r.Touch(MechKind.Stuck, "選擇畫面（A／B）", "1.60.1");
			MechReport.AddDetail(stuck, "試點 6 次沒反應");
			MechReport.AddDetail(stuck, "試點 6 次沒反應"); // 重複不加
			r.Touch(MechKind.Keyword, "starship", "1.60.1");
			var done = r.Touch(MechKind.Keyword, "old thing", "1.60.0");
			done.Resolved = true;

			Check("同一項目累計次數、範例與細節不重複", kw.Count == 2 && kw.Examples.Count == 1 && stuck.Details.Count == 1);

			r.GameEnded(new[] { (MechKind.Keyword, "starship"), (MechKind.Keyword, "starship"), (MechKind.Stuck, "選擇畫面（A／B）") }, false);
			r.GameEnded(new[] { (MechKind.Keyword, "starship") }, true);
			Check("對局結束累計出現場數與勝場", kw.GamesSeen == 2 && kw.Wins == 1 && stuck.GamesSeen == 1 && stuck.Wins == 0,
				$"關鍵字 {kw.Wins}/{kw.GamesSeen}、卡住 {stuck.Wins}/{stuck.GamesSeen}");

			var md = r.ToMarkdown("0.7.0", "1.60.1", "2026-11-15 20:30");
			var iStuck = md.IndexOf("【卡住的畫面】", StringComparison.Ordinal);
			var iKw = md.IndexOf("【新卡牌關鍵字】", StringComparison.Ordinal);
			Check("報告優先列出「卡住的畫面」", iStuck > 0 && iKw > iStuck);
			Check("已處理的項目不列入待處理", !md.Contains("old thing") && md.Contains("| 新卡牌關鍵字 | 1 | 1 |"));
			Check("報告含給 Claude 的說明、勝率與建議處理", md.Contains("給 Claude 的說明") && md.Contains("勝率 50%") && md.Contains("建議處理") && md.Contains("Bot/CardText.cs"));
			Check("表格中的「|」會跳脫", md.Contains("Test \\| Ship"));

			var back = MechReport.FromJson(r.ToJson());
			Check("報告資料 JSON 往返不失真", back.Entries.Count == 3 && back.Entries[0].Examples[0].Tags == "STARSHIP=1" && back.Entries[2].Resolved);
			var empty = new MechReport().ToMarkdown("0.7.0", "1.60.1");
			Check("沒有新機制時報告顯示「目前沒有待處理」", empty.Contains("目前沒有待處理"));
			File.WriteAllText(Path.Combine(Path.GetTempPath(), "hshelper-sample-report.md"), md);
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
		public delegate BotAction Policy(Snapshot s, ISet<int> skip);

		public class Side
		{
			public int Hp = 30, MaxMana, Mana, Fatigue;
			public bool HeroPowerUsed;
			public List<Ent> Deck = new List<Ent>(), Hand = new List<Ent>(), Board = new List<Ent>();
			public Ent Hero, Power;
			public int FaceDamageTaken, AoeEvents, MaxBurst, TurnsTaken; // 給學習功能用的統計（被對手打的）
		}

		// 牌組規格：同一副牌可以產生兩份給雙方（鏡像對戰）
		public class CardSpec { public char Type; public int Cost, Atk, Hp; public bool Charge, Taunt; public string Text, Name; }

		public static List<CardSpec> RandomDeckSpec(Random r, double aoeRate = 0.1, double burnRate = 0.1, double chargeRate = 1.0 / 12)
		{
			var deck = new List<CardSpec>();
			for(var i = 0; i < 30; i++)
			{
				var x = r.NextDouble();
				if(x < burnRate) { deck.Add(new CardSpec { Type = 'S', Cost = r.Next(1, 4), Text = "Deal 3 damage.", Name = "火球" }); continue; }
				if(x < burnRate + aoeRate) { deck.Add(new CardSpec { Type = 'S', Cost = 4, Text = "Deal 2 damage to all enemy minions.", Name = "爆炸" }); continue; }
				var c = new[] { 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 7 }[r.Next(14)];
				deck.Add(new CardSpec { Type = 'M', Cost = c, Atk = Math.Max(1, c + r.Next(-1, 2)), Hp = Math.Max(1, c + r.Next(0, 2)), Charge = r.NextDouble() < chargeRate, Taunt = r.Next(5) == 0 });
			}
			return deck;
		}

		private static List<Ent> Instantiate(List<CardSpec> spec, Random shuffle)
		{
			var list = spec.Select(c => c.Type == 'S' ? Spell(c.Cost, c.Text, c.Name) : Card(c.Cost, c.Atk, c.Hp, charge: c.Charge, taunt: c.Taunt)).ToList();
			return list.OrderBy(_ => shuffle.Next()).ToList();
		}

		private static void Draw(Side s)
		{
			if(s.Deck.Count == 0) { s.Hp -= ++s.Fatigue; return; }
			var c = s.Deck[0];
			s.Deck.RemoveAt(0);
			if(s.Hand.Count < 10) s.Hand.Add(c);
		}

		public static BotAction NewAi(Snapshot s, ISet<int> skip) => Search.BestFirstAction(s, skip, out _);
		public static BotAction OldAi(Snapshot s, ISet<int> skip) => HsHelper.Baseline.SearchV03.BestFirstAction(s, skip, out _);

		/// <summary>只會打臉：高費先出、能打臉就打臉（有嘲諷才打嘲諷）。</summary>
		public static BotAction FaceBot(Snapshot s, ISet<int> skip) => SimpleBot(s, skip, false);
		/// <summary>會交換：能「殺掉又活著」就交換，其餘打臉。</summary>
		public static BotAction TraderBot(Snapshot s, ISet<int> skip) => SimpleBot(s, skip, true);

		private static BotAction SimpleBot(Snapshot s, ISet<int> skip, bool trade)
		{
			var coin = s.Hand.FirstOrDefault(c => c.IsCoin);
			if(coin != null && s.Hand.Any(c => !c.IsCoin && c.Cost == s.Mana + 1))
				return new BotAction { Type = ActionType.Play, Source = coin };
			var card = s.Hand.Where(c => !c.IsCoin && !skip.Contains(c.Id) && c.Cost <= s.Mana && (c.Kind != Kind.Minion || s.MyBoard.Count < 7))
				.OrderByDescending(c => c.Cost).FirstOrDefault();
			if(card != null)
			{
				Ent target = null;
				if(card.Kind == Kind.Spell && card.Target != TargetKind.None)
					target = trade
						? s.OppBoard.Where(m => m.Hp <= card.Damage).OrderByDescending(m => m.Atk).FirstOrDefault() ?? s.OppHero
						: s.OppBoard.OrderByDescending(m => m.Atk).FirstOrDefault() ?? s.OppHero;
				return new BotAction { Type = ActionType.Play, Source = card, Target = target };
			}
			if(s.MyHeroPower != null && s.Mana >= s.MyHeroPower.Cost)
				return new BotAction { Type = ActionType.HeroPower, Source = s.MyHeroPower };
			var attacker = s.MyBoard.FirstOrDefault(m => !skip.Contains(m.Id) && m.Atk > 0 && (!m.Exhausted || m.Charge) && m.AttacksThisTurn == 0);
			if(attacker != null)
			{
				var taunt = s.OppBoard.FirstOrDefault(x => x.Taunt);
				Ent target = taunt ?? s.OppHero;
				if(taunt == null && trade)
					target = s.OppBoard.Where(d => attacker.Atk >= d.Hp && attacker.Hp > d.Atk).OrderByDescending(d => d.Atk).FirstOrDefault() ?? s.OppHero;
				return new BotAction { Type = ActionType.Attack, Source = attacker, Target = target };
			}
			return null;
		}

		/// <summary>一場對戰；回傳 true = A 贏。</summary>
		public static bool PlayGame(Policy a, Policy b, List<CardSpec> deckA, List<CardSpec> deckB, bool aFirst, Random r, ref int illegal,
			Side[] sidesOut = null)
		{
			var A = new Side { Hero = Hero(30), Power = HeroPower("Deal 2 damage to the enemy hero.") };
			var B = new Side { Hero = Hero(30), Power = HeroPower("Deal 2 damage to the enemy hero.") };
			A.Deck = Instantiate(deckA, r);
			B.Deck = Instantiate(deckB, r);
			for(var i = 0; i < 3; i++) { Draw(A); Draw(B); }
			Draw(aFirst ? B : A);
			(aFirst ? B : A).Hand.Add(Coin());
			if(sidesOut != null) { sidesOut[0] = A; sidesOut[1] = B; }

			var turn = 0;
			var aTurn = aFirst;
			while(A.Hp > 0 && B.Hp > 0 && turn < 80)
			{
				var me = aTurn ? A : B;
				var op = aTurn ? B : A;
				me.MaxMana = Math.Min(10, me.MaxMana + 1);
				me.Mana = me.MaxMana;
				me.HeroPowerUsed = false;
				foreach(var m in me.Board) { m.Exhausted = false; m.AttacksThisTurn = 0; m.TurnsInPlay++; m.Frozen = false; }
				Draw(me);
				var opHpBefore = op.Hp;
				var opBoardBefore = op.Board.Count;
				illegal += RunTurn(aTurn ? a : b, me, op);
				op.FaceDamageTaken += Math.Max(0, opHpBefore - op.Hp);
				op.MaxBurst = Math.Max(op.MaxBurst, opHpBefore - op.Hp);
				op.TurnsTaken++;
				if(opBoardBefore - op.Board.Count >= 2) op.AoeEvents++;
				aTurn = !aTurn;
				turn++;
			}
			return A.Hp > 0 && B.Hp <= 0;
		}

		private static int RunTurn(Policy p, Side me, Side op)
		{
			var bad = 0;
			var skip = new HashSet<int>();
			for(var step = 0; step < 40 && me.Hp > 0 && op.Hp > 0; step++)
			{
				var s = FromSides(me, op);
				var act = p(s, skip);
				if(act == null || act.Type == ActionType.EndTurn)
					break;
				if(!IsLegal(s, act, out _)) { bad++; skip.Add(act.Source.Id); continue; }
				Apply(me, op, act);
			}
			return bad;
		}

		private static void SelfPlay(int games)
		{
			void Match(string name, Policy a, Policy b, int n, double minRate, bool mirror)
			{
				var rng = new Random(2026);
				int wins = 0, illegal = 0;
				for(var g = 0; g < n; g++)
				{
					var deckA = RandomDeckSpec(rng);
					var deckB = mirror ? deckA : RandomDeckSpec(rng);
					if(PlayGame(a, b, deckA, deckB, g % 2 == 0, rng, ref illegal))
						wins++;
				}
				var rate = wins * 100.0 / n;
				Check($"{name}：勝率 ≥ {minRate}%", rate >= minRate && illegal == 0, $"{wins}/{n}（{rate:F1}%），不合法動作 {illegal}");
			}

			Match("新版 AI vs 只會打臉的對手（隨機牌組）", NewAi, FaceBot, games, 65, false);
			Match("新版 AI vs 會交換的對手（隨機牌組）", NewAi, TraderBot, games, 60, false);
			Match("新版 AI vs 舊版 v0.3 AI（鏡像牌組，輪流先手）", NewAi, OldAi, games, 50, true);
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
