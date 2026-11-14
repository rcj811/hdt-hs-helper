using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HsHelper.Bot
{
	/// <summary>打法：平衡／進攻／控場。</summary>
	public enum Style { Balanced = 0, Aggro = 1, Control = 2 }

	/// <summary>一場對局的學習紀錄（每筆有唯一 Id，匯入時用來比對去重）。</summary>
	[DataContract]
	public class GameRecord
	{
		[DataMember(Order = 1)] public string Id = Guid.NewGuid().ToString("N");
		[DataMember(Order = 2)] public string Utc = DateTime.UtcNow.ToString("o");
		[DataMember(Order = 3)] public string Source = "real";      // real = 真實對局，sim = 模擬器
		[DataMember(Order = 4)] public string OppClass = "";
		[DataMember(Order = 5)] public List<string> OppCards = new List<string>();
		[DataMember(Order = 6)] public bool Won;
		[DataMember(Order = 7)] public int Style = -1;              // -1 = 手動打的（不列入打法統計）
		[DataMember(Order = 8)] public int OppTurns;
		[DataMember(Order = 9)] public int FaceDmgTaken;            // 對手總共打我臉多少
		[DataMember(Order = 10)] public int MaxBurst;               // 對手單回合最大傷害
		[DataMember(Order = 11)] public int AoeEvents;              // 對手單回合清掉我 2 隻以上手下的次數
		[DataMember(Order = 12)] public long DeckId;                // 我用的套牌（遊戲內的套牌編號）
		[DataMember(Order = 13)] public string DeckName = "";

		public double AggroRate => OppTurns > 0 ? FaceDmgTaken / (double)OppTurns : 0;
		public double AoeRate => OppTurns > 0 ? AoeEvents * 5.0 / OppTurns : 0; // 每 5 回合幾次
	}

	[DataContract]
	public class LearningDb
	{
		[DataMember(Order = 1)] public int Version = 1;
		[DataMember(Order = 2)] public List<GameRecord> Games = new List<GameRecord>();

		private HashSet<string> _ids;
		private HashSet<string> Ids => _ids ??= new HashSet<string>(Games.Select(g => g.Id));

		/// <summary>合併：逐筆比對 Id，資料庫沒有的才加入。</summary>
		public (int added, int skipped) Merge(IEnumerable<GameRecord> records)
		{
			int added = 0, skipped = 0;
			foreach(var r in records)
			{
				if(r == null || string.IsNullOrEmpty(r.Id) || !Ids.Add(r.Id))
				{
					skipped++;
					continue;
				}
				Games.Add(r);
				added++;
			}
			return (added, skipped);
		}

		public void Add(GameRecord r) => Merge(new[] { r });

		// ───────── 存檔／匯出／匯入 ─────────
		private static readonly DataContractJsonSerializer Ser = new DataContractJsonSerializer(typeof(LearningDb));

		public string ToJson()
		{
			using var ms = new MemoryStream();
			Ser.WriteObject(ms, this);
			return Encoding.UTF8.GetString(ms.ToArray());
		}

		public static LearningDb FromJson(string json)
		{
			using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
			var db = (LearningDb)Ser.ReadObject(ms);
			db.Games ??= new List<GameRecord>();
			db._ids = null;
			return db;
		}

		public static LearningDb Load(string path)
		{
			try
			{
				return File.Exists(path) ? FromJson(File.ReadAllText(path, Encoding.UTF8)) : new LearningDb();
			}
			catch(Exception)
			{
				// 檔案壞掉：備份後重新開始，不影響遊戲
				try { File.Copy(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch(Exception) { }
				return new LearningDb();
			}
		}

		public void Save(string path)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, ToJson(), Encoding.UTF8);
			if(File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
		}

		public (int added, int skipped) ImportFile(string path) => Merge(FromJson(File.ReadAllText(path, Encoding.UTF8)).Games);
	}

	/// <summary>對手資料：從相似對局彙整出來的打法傾向與各打法勝率。</summary>
	public class OpponentProfile
	{
		public double Weight;                    // 參考資料量（加權後場數）
		public double AggroRate, AoeRate, Burst; // 對手打臉速度、AOE 頻率、爆發
		public double[] StyleGames = new double[3], StyleWins = new double[3];
		public string Summary = "";
	}

	/// <summary>
	/// 從過去對局學習：找出相似對手（同職業且出過的牌重疊 → 權重高；同職業 → 中；打法數據相近的模擬資料 → 低），
	/// 估計對手類型，再用 UCB1 在三種打法中挑勝率最好的（輸了就會換別的試）。
	/// </summary>
	public static class Learner
	{
		public static OpponentProfile Profile(LearningDb db, string oppClass, ICollection<string> seenCards,
			double liveAggro = -1, double liveAoe = -1)
		{
			var p = new OpponentProfile();
			if(db == null || db.Games.Count == 0)
				return p;

			double sw = 0, sa = 0, so = 0, sb = 0;
			var seen = seenCards != null ? new HashSet<string>(seenCards) : new HashSet<string>();
			foreach(var g in db.Games)
			{
				double w;
				if(g.Source == "real" && !string.IsNullOrEmpty(oppClass) && g.OppClass == oppClass)
				{
					w = 0.4;
					if(seen.Count >= 3 && g.OppCards.Count > 0)
					{
						var inter = g.OppCards.Count(seen.Contains);
						var jac = inter / (double)(seen.Count + g.OppCards.Count - inter);
						w += jac * 0.6; // 同一副牌（牌重疊多）權重最高，最多 1.0（一場算一場）
					}
				}
				else
				{
					// 模擬資料或其他職業：只在打法數據相近時參考
					if(liveAggro < 0)
						w = g.Source == "sim" ? 0.05 : 0.02;
					else
					{
						var d = Math.Abs(g.AggroRate - liveAggro) / 3 + Math.Abs(g.AoeRate - Math.Max(0, liveAoe));
						w = (g.Source == "sim" ? 0.3 : 0.15) * Math.Exp(-d * 2);
					}
				}
				sw += w;
				sa += w * g.AggroRate;
				so += w * g.AoeRate;
				sb += w * g.MaxBurst;
				if(g.Style >= 0 && g.Style < 3)
				{
					p.StyleGames[g.Style] += w;
					if(g.Won) p.StyleWins[g.Style] += w;
				}
			}
			if(sw <= 0)
				return p;
			p.Weight = sw;
			p.AggroRate = sa / sw;
			p.AoeRate = so / sw;
			p.Burst = sb / sw;
			// 對局中即時觀察到的數據比歷史更可靠
			if(liveAggro >= 0) p.AggroRate = (p.AggroRate + liveAggro * 2) / 3;
			if(liveAoe >= 0) p.AoeRate = (p.AoeRate + liveAoe * 2) / 3;
			return p;
		}

		/// <summary>
		/// 選打法（「輸了才學」＋ UCB1）：
		///  - 平衡打法資料還少（&lt; 3 場）或勝率 ≥ 55% → 維持平衡（打得贏就不亂改）
		///  - 對這種對手常輸 → 用 UCB1 在三種打法中挑：勝率高的優先，但場數少的也會試
		/// 模擬器實驗顯示：對一般對手三種打法差距在統計誤差內，所以預設不更動。
		/// </summary>
		public static Style ChooseStyle(OpponentProfile p)
		{
			var n0 = p.StyleGames[0];
			if(n0 < 3 || (p.StyleWins[0] + 1) / (n0 + 2) >= 0.55)
				return Style.Balanced;

			var total = p.StyleGames.Sum();
			var best = Style.Balanced;
			var bestScore = double.MinValue;
			for(var i = 0; i < 3; i++)
			{
				var n = p.StyleGames[i];
				var mean = (p.StyleWins[i] + 1) / (n + 2); // 拉普拉斯平滑：沒資料時當 50%
				var score = mean + 0.4 * Math.Sqrt(Math.Log(total + 1) / (n + 1));
				if(score > bestScore)
				{
					bestScore = score;
					best = (Style)i;
				}
			}
			return best;
		}

		/// <summary>依打法與對手類型調整權重。</summary>
		public static Weights Adjust(Weights baseW, Style style, OpponentProfile p)
		{
			var w = baseW.Clone();
			switch(style)
			{
				case Style.Aggro:
					w.AggroFace *= 1.6;
					w.OppHp *= 1.4;
					w.OppLowLine += 4;
					break;
				case Style.Control:
					w.HeroHp *= 1.5;
					w.LowHpLine += 4;
					w.OppMinionAtk *= 1.3;
					w.AggroFace *= 0.7;
					break;
			}
			if(p != null && p.Weight > 0.3)
			{
				if(p.AggroRate >= 4) // 對手很會打臉：重視血量、提早防守
				{
					w.LowHpLine += Math.Min(6, p.AggroRate - 2);
					w.HeroHp *= 1.3;
					w.LethalRisk *= 1.5;
				}
				if(p.AoeRate >= 0.6) // 對手常用 AOE：不要一次鋪太多手下
					w.Overextend = Math.Min(12, 4 * p.AoeRate);
				w.EnemyBurst = Math.Max(2, Math.Min(10, p.Burst * 0.5));
			}
			return w;
		}

		public static string Describe(Style s, OpponentProfile p)
		{
			var name = s == Style.Aggro ? "進攻" : s == Style.Control ? "控場" : "平衡";
			if(p == null || p.Weight <= 0.3)
				return name;
			var tags = new List<string>();
			if(p.AggroRate >= 4) tags.Add("對手偏打臉");
			if(p.AoeRate >= 0.6) tags.Add("對手常用AOE");
			if(p.Burst >= 8) tags.Add("對手爆發高");
			return tags.Count > 0 ? $"{name}（{string.Join("、", tags)}）" : name;
		}
	}

	/// <summary>套牌資訊（卡表統計 + AI 適合度）。</summary>
	public class DeckInfo
	{
		public long Id;
		public string Name = "";
		public int Slot = -1;          // 在選套牌畫面這一頁的第幾格（0 起算）
		public int Cards, Minions, Complex;
		public double AvgCost;
		public double Suitability;     // 0～1：越高越適合 AI 打
		public int Games, Wins;
		public double Expected;        // 預估勝率（實戰紀錄 + 適合度先驗）
	}

	/// <summary>
	/// 自動選套牌：挑「預估勝率最高」的一副。
	/// 預估勝率 = (實戰勝場 + 先驗 × 4) ÷ (實戰場數 + 4)，先驗由 AI 適合度換算（40%～60%）。
	/// 打得越多越以實戰紀錄為準；某副牌一直輸，預估勝率下降，就會自動換到別副試試。
	/// </summary>
	public static class DeckChooser
	{
		private const double PriorGames = 4;

		// AI 不擅長的機制（看到這些字樣就算「複雜卡」）
		private static readonly string[] ComplexWords =
		{
			"discover", "choose one", "combo", "secret", "quest", "corrupt", "infuse", "forge", "outcast", "trade",
			"shuffle", "swap", "copy", "cast it", "for each", "if you", "whenever", "spellburst", "manathirst", "excavate",
			"colossal", "titan", "location", "dormant", "magnetic", "echo", "twinspell", "overload"
		};

		/// <summary>cards：每張牌 (是否手下, 費用, 英文敘述, 張數)。</summary>
		public static DeckInfo Analyze(long id, string name, IEnumerable<(bool minion, int cost, string text, int count)> cards)
		{
			var d = new DeckInfo { Id = id, Name = name ?? "" };
			double costSum = 0;
			foreach(var (minion, cost, text, count) in cards)
			{
				d.Cards += count;
				if(minion) d.Minions += count;
				costSum += cost * count;
				var t = (text ?? "").ToLowerInvariant();
				if(ComplexWords.Any(t.Contains))
					d.Complex += count;
			}
			if(d.Cards == 0)
				return d;
			d.AvgCost = costSum / d.Cards;
			var minionScore = Math.Min(1, d.Minions / (d.Cards * 0.6));              // 手下佔 6 成以上滿分
			var curveScore = Math.Max(0, Math.Min(1, 1 - (d.AvgCost - 2.5) / 4));    // 平均費用越低越好
			var simpleScore = 1 - d.Complex / (double)d.Cards;                       // AI 看得懂的比例
			d.Suitability = 0.4 * minionScore + 0.3 * curveScore + 0.3 * simpleScore;
			return d;
		}

		public static DeckInfo Choose(IList<DeckInfo> decks, LearningDb db, out string reason)
		{
			reason = "";
			if(decks == null || decks.Count == 0)
				return null;
			foreach(var d in decks)
			{
				var recs = db?.Games.Where(g => g.DeckId == d.Id && g.DeckId != 0).ToList() ?? new List<GameRecord>();
				d.Games = recs.Count;
				d.Wins = recs.Count(g => g.Won);
				var prior = 0.40 + 0.20 * d.Suitability;
				d.Expected = (d.Wins + prior * PriorGames) / (d.Games + PriorGames);
			}
			var best = decks.OrderByDescending(d => d.Expected).ThenByDescending(d => d.Suitability).First();
			reason = best.Games > 0
				? $"{best.Name}：{best.Wins}勝/{best.Games}場，預估勝率 {best.Expected:P0}"
				: $"{best.Name}：還沒紀錄，AI 適合度 {best.Suitability:P0}";
			return best;
		}
	}
}
