using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HsHelper.Bot;

namespace HsHelper.Tests
{
	/// <summary>
	/// 演化演算法調權重（(1+λ)-ES，參考 Hearthstone AI 競賽中用 EA 調評分函式權重的做法）。
	/// 適應度 = 鏡像牌組對舊版 AI 的勝場 + 對交換型對手的勝場；所有候選用同一組隨機種子比較（降低雜訊）。
	/// 用法：dotnet run -- --tune [世代數]
	/// </summary>
	public static class Tuner
	{
		private static readonly string[] Names =
		{
			"HeroHp", "LowHpPenalty", "LowHpLine", "OppHp", "OppLowBonus", "OppLowLine", "MyMinionBase", "MyMinionAtk",
			"MyMinionHp", "OppMinionBase", "OppMinionAtk", "OppMinionHp", "OppAtk4", "OppTaunt", "HandCard", "ManaLeft",
			"LethalRisk", "AggroFace", "EnemyTurnBlend"
		};

		private static FieldInfo F(string n) => typeof(Weights).GetField(n);

		public static int Fitness(Weights w, int games, int seed)
		{
			Search.W = w;
			var wins = 0;
			var illegal = 0;
			var rng = new Random(seed);
			for(var g = 0; g < games; g++)
			{
				var deck = Program.RandomDeckSpec(rng);
				if(Program.PlayGame(Program.NewAi, Program.OldAi, deck, deck, g % 2 == 0, new Random(seed * 7919 + g), ref illegal))
					wins++;
			}
			var rng2 = new Random(seed + 1);
			for(var g = 0; g < games / 2; g++)
			{
				var dA = Program.RandomDeckSpec(rng2);
				var dB = Program.RandomDeckSpec(rng2);
				if(Program.PlayGame(Program.NewAi, Program.TraderBot, dA, dB, g % 2 == 0, new Random(seed * 104729 + g), ref illegal))
					wins++;
			}
			return wins;
		}

		public static void Run(int generations)
		{
			var rnd = new Random(42);
			Search.BeamWidth = 80;
			Search.MaxDepth = 10;
			const int games = 160;
			var parent = new Weights();
			var parentFit = Fitness(parent, games, 1);
			Console.WriteLine($"起點適應度 {parentFit}");
			var sigma = 0.25;
			for(var gen = 0; gen < generations; gen++)
			{
				var improved = false;
				for(var k = 0; k < 4; k++)
				{
					var child = parent.Clone();
					foreach(var n in Names)
					{
						if(rnd.NextDouble() > 0.35)
							continue;
						var f = F(n);
						var v = (double)f.GetValue(child);
						var nv = v * Math.Exp(sigma * Gauss(rnd));
						if(n == "EnemyTurnBlend") nv = Math.Max(0, Math.Min(1, nv));
						f.SetValue(child, nv);
					}
					var fit = Fitness(child, games, 1);
					if(fit > parentFit)
					{
						parent = child;
						parentFit = fit;
						improved = true;
					}
				}
				sigma = improved ? Math.Min(0.5, sigma * 1.2) : Math.Max(0.05, sigma * 0.85);
				Console.WriteLine($"世代 {gen + 1}：適應度 {parentFit}，σ={sigma:F2}");
			}

			// 用沒看過的種子驗證，避免只是剛好適合訓練種子
			var baseline = Fitness(new Weights(), 300, 999);
			var tuned = Fitness(parent, 300, 999);
			Console.WriteLine($"驗證（新種子）：預設權重 {baseline}，調整後 {tuned}");
			Console.WriteLine("最佳權重：");
			foreach(var n in Names)
				Console.WriteLine($"\t\tpublic double {n} = {(double)F(n).GetValue(parent):0.###};");
		}

		private static double Gauss(Random r)
		{
			var u1 = 1.0 - r.NextDouble();
			var u2 = r.NextDouble();
			return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
		}
	}
}
