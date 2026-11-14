using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HsHelper.Bot;

namespace HsHelper.Tests
{
	/// <summary>
	/// 學習功能測試：
	///  1. 匯入／匯出：逐筆比對 Id，重複的略過、沒有的加入；JSON 往返不失真；壞檔不當機
	///  2. 從對局學習：對上固定打法的對手連打，「有學習」的勝率不能比「沒學習」差
	///  3. 產生內建的模擬器學習資料（--seed）
	/// </summary>
	public static class LearningTests
	{
		// 三種對手類型
		public class Archetype
		{
			public string Name;
			public double Aoe, Burn, Charge;
			public Program.Policy Bot;
		}

		public static readonly Archetype[] Archetypes =
		{
			new Archetype { Name = "打臉型", Aoe = 0.03, Burn = 0.25, Charge = 0.3, Bot = Program.FaceBot },
			new Archetype { Name = "AOE型", Aoe = 0.3, Burn = 0.05, Charge = 0.05, Bot = Program.TraderBot },
			new Archetype { Name = "交換型", Aoe = 0.1, Burn = 0.1, Charge = 1.0 / 12, Bot = Program.TraderBot },
		};

		private static GameRecord PlayRecorded(Weights w, Style style, Archetype a, Random rng, int seed, string id, string source,
			string oppClass, List<string> oppCards, out bool won)
		{
			Search.W = w;
			var myDeck = Program.RandomDeckSpec(rng);
			var oppDeck = Program.RandomDeckSpec(rng, a.Aoe, a.Burn, a.Charge);
			var sides = new Program.Side[2];
			var illegal = 0;
			won = Program.PlayGame(Program.NewAi, a.Bot, myDeck, oppDeck, seed % 2 == 0, new Random(seed), ref illegal, sides);
			var me = sides[0];
			return new GameRecord
			{
				Id = id, Source = source, OppClass = oppClass, OppCards = oppCards ?? new List<string>(),
				Won = won, Style = (int)style, OppTurns = me.TurnsTaken, FaceDmgTaken = me.FaceDamageTaken,
				MaxBurst = me.MaxBurst, AoeEvents = me.AoeEvents
			};
		}

		/// <summary>產生模擬器學習資料：每種對手 × 每種打法各打 n 場。Id 固定，重新產生也能正確去重。</summary>
		public static void GenerateSeed(string path, int n)
		{
			Search.BeamWidth = 120;
			var db = new LearningDb();
			var baseW = new Weights();
			for(var ai = 0; ai < Archetypes.Length; ai++)
			{
				var a = Archetypes[ai];
				for(var st = 0; st < 3; st++)
				{
					var rng = new Random(5000 + ai * 100 + st);
					var wins = 0;
					for(var i = 0; i < n; i++)
					{
						var rec = PlayRecorded(Learner.Adjust(baseW, (Style)st, null), (Style)st, a, rng, 70000 + ai * 1000 + i,
							$"sim-v1-{ai}-{st}-{i}", "sim", "SIM-" + a.Name, null, out var won);
						if(won) wins++;
						db.Add(rec);
					}
					Console.WriteLine($"{a.Name} × {(Style)st}: {wins}/{n}");
				}
			}
			db.Save(path);
			Search.W = new Weights();
			Console.WriteLine($"已寫入 {db.Games.Count} 筆到 {path}");
		}

		/// <summary>實驗：每種對手 × 每種設定各打 n 場，量出實際效果（--exp）。</summary>
		public static void Experiment(int n)
		{
			var baseW = new Weights();
			foreach(var a in Archetypes)
			{
				// 先打一輪蒐集這種對手的打法數據，建立「理想的對手資料」
				var db = new LearningDb();
				var r0 = new Random(1);
				for(var i = 0; i < 40; i++)
					db.Add(PlayRecorded(baseW, Style.Balanced, a, r0, 1000 + i, Guid.NewGuid().ToString("N"), "real", "T", new List<string> { "a", "b", "c" }, out _));
				var prof = Learner.Profile(db, "T", new List<string> { "a", "b", "c" });
				Console.WriteLine($"{a.Name}：對手數據 打臉 {prof.AggroRate:F1}/回合、AOE {prof.AoeRate:F2}、爆發 {prof.Burst:F1}");
				foreach(var (label, w) in new[]
				{
					("平衡", Learner.Adjust(baseW, Style.Balanced, null)),
					("進攻", Learner.Adjust(baseW, Style.Aggro, null)),
					("控場", Learner.Adjust(baseW, Style.Control, null)),
					("平衡＋對手調整", Learner.Adjust(baseW, Style.Balanced, prof)),
					("進攻＋對手調整", Learner.Adjust(baseW, Style.Aggro, prof)),
				})
				{
					var rng = new Random(777);
					var wins = 0;
					for(var i = 0; i < n; i++)
					{
						PlayRecorded(w, Style.Balanced, a, rng, 200000 + i, "x", "real", "T", null, out var won);
						if(won) wins++;
					}
					Console.WriteLine($"   {label}: {wins}/{n} ({wins * 100.0 / n:F1}%)");
				}
			}
			Search.W = new Weights();
		}

		public static void Run(string seedPath)
		{
			ImportExport(seedPath);
			OnlineLearning();
			Search.W = new Weights();
		}

		private static void ImportExport(string seedPath)
		{
			var a = new LearningDb();
			var r1 = new GameRecord { OppClass = "MAGE", OppCards = new List<string> { "CS2_029", "CS2_024" }, Won = true, Style = 1, OppTurns = 8, FaceDmgTaken = 20, MaxBurst = 6, AoeEvents = 1 };
			var r2 = new GameRecord { OppClass = "HUNTER", Won = false, Style = 0, OppTurns = 6, FaceDmgTaken = 30 };
			var r3 = new GameRecord { OppClass = "PRIEST", Won = true, Style = 2, OppTurns = 12, FaceDmgTaken = 10 };
			a.Merge(new[] { r1, r2, r3 });
			var json = a.ToJson();

			var again = a.Merge(LearningDb.FromJson(json).Games);
			Program.Check("匯入同一份資料：全部略過不重複", again.added == 0 && again.skipped == 3 && a.Games.Count == 3, $"新增 {again.added}，略過 {again.skipped}");

			var b = LearningDb.FromJson(json);
			var r4 = new GameRecord { OppClass = "ROGUE", Won = false, Style = 1 };
			var r5 = new GameRecord { OppClass = "DRUID", Won = true, Style = 0 };
			b.Merge(new[] { r4, r5 });
			var merged = a.Merge(LearningDb.FromJson(b.ToJson()).Games);
			Program.Check("合併別人的資料：只加入沒有的", merged.added == 2 && merged.skipped == 3 && a.Games.Count == 5, $"新增 {merged.added}，略過 {merged.skipped}");

			var rt = LearningDb.FromJson(json).Games.First(g => g.Id == r1.Id);
			Program.Check("匯出再匯入資料完整", rt.OppClass == "MAGE" && rt.OppCards.Count == 2 && rt.Won && rt.Style == 1 && rt.FaceDmgTaken == 20 && rt.MaxBurst == 6);

			var tmp = Path.Combine(Path.GetTempPath(), "hshelper-bad-" + Guid.NewGuid().ToString("N") + ".json");
			File.WriteAllText(tmp, "{ 這不是 json");
			var bad = LearningDb.Load(tmp);
			Program.Check("學習檔壞掉不會當機（自動備份後重建）", bad.Games.Count == 0);
			foreach(var f in Directory.GetFiles(Path.GetTempPath(), "hshelper-bad-*")) File.Delete(f);

			// 大量資料匯入速度
			var big = new LearningDb();
			big.Merge(Enumerable.Range(0, 20000).Select(i => new GameRecord { OppClass = "X" + i % 11 }));
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var bigJson = big.ToJson();
			var imported = new LearningDb();
			imported.Merge(LearningDb.FromJson(bigJson).Games);
			var dup = imported.Merge(LearningDb.FromJson(bigJson).Games);
			sw.Stop();
			Program.Check("2 萬筆資料匯入＋比對 < 5 秒", sw.ElapsedMilliseconds < 5000 && dup.skipped == 20000, $"{sw.ElapsedMilliseconds} ms");

			if(File.Exists(seedPath))
			{
				var seed = LearningDb.Load(seedPath);
				var fresh = new LearningDb();
				var first = fresh.Merge(seed.Games);
				var second = fresh.Merge(LearningDb.Load(seedPath).Games);
				Program.Check("內建模擬學習資料可載入且不會重複匯入", seed.Games.Count > 0 && first.added == seed.Games.Count && second.added == 0,
					$"{seed.Games.Count} 筆");
			}
			else
				Program.Check("內建模擬學習資料存在", false, seedPath);
		}

		/// <summary>
		/// 線上學習：對每種對手連打 N 場。
		/// 沒學習＝固定平衡打法；有學習＝每場依過去紀錄挑打法與調整權重，打完記錄。
		/// </summary>
		/// <summary>對上「打不贏」的對手（牌組數值較強）：確認學習會在輸了之後換打法嘗試，而不是一直用同一種。</summary>
		private static void HardOpponent()
		{
			var baseW = new Weights();
			var db = new LearningDb();
			var cards = new List<string> { "h1", "h2", "h3" };
			var rng = new Random(4242);
			int wins = 0;
			const int n = 60;
			for(var i = 0; i < n; i++)
			{
				var p = Learner.Profile(db, "HARD", cards);
				var style = Learner.ChooseStyle(p);
				Search.W = Learner.Adjust(baseW, style, p);
				var myDeck = Program.RandomDeckSpec(rng);
				var oppDeck = Program.RandomDeckSpec(rng);
				foreach(var c in oppDeck.Where(c => c.Type == 'M')) { c.Atk += 2; c.Hp += 2; } // 對手牌組明顯較強
				var sides = new Program.Side[2];
				var illegal = 0;
				var won = Program.PlayGame(Program.NewAi, Program.TraderBot, myDeck, oppDeck, i % 2 == 0, new Random(60000 + i), ref illegal, sides);
				if(won) wins++;
				db.Add(new GameRecord { OppClass = "HARD", OppCards = cards, Won = won, Style = (int)style, OppTurns = sides[0].TurnsTaken, FaceDmgTaken = sides[0].FaceDamageTaken });
			}
			var used = Enumerable.Range(0, 3).Count(k => db.Games.Count(g => g.Style == k) >= 3);
			var dist = string.Join(" ", Enumerable.Range(0, 3).Select(k => $"{(Style)k}:{db.Games.Count(g => g.Style == k)}場"));
			Program.Check("常輸的對手：會換打法嘗試（至少試過 2 種）", used >= 2, $"勝 {wins}/{n}；{dist}");
		}

		private static void OnlineLearning()
		{
			const int n = 150;
			var baseW = new Weights();
			int totalOff = 0, totalOn = 0;
			foreach(var a in Archetypes)
			{
				// 沒學習
				var rng = new Random(9100);
				var off = 0;
				for(var i = 0; i < n; i++)
				{
					PlayRecorded(baseW, Style.Balanced, a, rng, 50000 + i, "x", "real", "C", null, out var won);
					if(won) off++;
				}
				// 有學習（從空資料庫開始，只靠這一連串對局學）
				rng = new Random(9100);
				var db = new LearningDb();
				var on = 0;
				var oppCards = new List<string> { a.Name + "-card1", a.Name + "-card2", a.Name + "-card3" };
				for(var i = 0; i < n; i++)
				{
					var p = Learner.Profile(db, "TESTCLASS", oppCards);
					var style = Learner.ChooseStyle(p);
					var rec = PlayRecorded(Learner.Adjust(baseW, style, p), style, a, rng, 50000 + i, Guid.NewGuid().ToString("N"), "real", "TESTCLASS", oppCards, out var won);
					db.Add(rec);
					if(won) on++;
				}
				var styles = string.Join(" ", Enumerable.Range(0, 3).Select(k => $"{(Style)k}:{db.Games.Count(g => g.Style == k)}場"));
				Console.WriteLine($"  {a.Name}：沒學習 {off}/{n}，有學習 {on}/{n}（{styles}）");
				Program.Check($"對{a.Name}對手：有學習不比沒學習差（150 場容許 4% 誤差）", on >= off - n * 0.04,
					$"沒學習 {off * 100.0 / n:F1}% → 有學習 {on * 100.0 / n:F1}%；打法分布 {styles}");
				totalOff += off;
				totalOn += on;
			}
			Program.Check("三種對手合計：有學習不比沒學習差（容許 2% 誤差）", totalOn >= totalOff - n * 3 * 0.02, $"{totalOff} → {totalOn}（共 {n * 3} 場）");
			HardOpponent();
		}
	}
}
