using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HsHelper.Bot
{
	/// <summary>報告項目類型。</summary>
	public static class MechKind
	{
		public const string Reader = "HDT 新增遊戲讀取功能";
		public const string Mode = "新玩法";
		public const string Keyword = "新卡牌關鍵字";
		public const string Stuck = "卡住的畫面";
		public const string PlayFail = "AI 打不出來的卡";
		public const string HdtUpdate = "HDT 更新";
	}

	[DataContract]
	public class CardExample
	{
		[DataMember(Order = 1)] public string CardId = "";
		[DataMember(Order = 2)] public string NameZh = "";
		[DataMember(Order = 3)] public string NameEn = "";
		[DataMember(Order = 4)] public string Type = "";
		[DataMember(Order = 5)] public int Cost, Atk, Hp;
		[DataMember(Order = 6)] public string TextEn = "";
		[DataMember(Order = 7)] public string Tags = "";      // 例：TAUNT=1; DARK_GIFT_ENTITY=1
		[DataMember(Order = 8)] public string AiReading = ""; // AI 怎麼理解這張卡（目標類型、傷害值…）
	}

	[DataContract]
	public class MechEntry
	{
		[DataMember(Order = 1)] public string Kind = "";
		[DataMember(Order = 2)] public string Key = "";
		[DataMember(Order = 3)] public string FirstSeen = "";
		[DataMember(Order = 4)] public string LastSeen = "";
		[DataMember(Order = 5)] public int Count;
		[DataMember(Order = 6)] public string HdtVersion = "";
		[DataMember(Order = 7)] public List<string> Details = new List<string>();
		[DataMember(Order = 8)] public List<CardExample> Examples = new List<CardExample>();
		[DataMember(Order = 9)] public int GamesSeen, Wins;
		[DataMember(Order = 10)] public List<string> Screenshots = new List<string>();
		[DataMember(Order = 11)] public bool Resolved;      // 之後版本已支援（由開發者標記）
	}

	[DataContract]
	public class MechReport
	{
		[DataMember(Order = 1)] public int Version = 1;
		[DataMember(Order = 2)] public List<MechEntry> Entries = new List<MechEntry>();

		public const int MaxDetails = 8, MaxExamples = 6, MaxShots = 3;

		public MechEntry Touch(string kind, string key, string hdtVersion, string now = null)
		{
			now ??= DateTime.Now.ToString("yyyy-MM-dd HH:mm");
			var e = Entries.FirstOrDefault(x => x.Kind == kind && x.Key == key);
			if(e == null)
			{
				e = new MechEntry { Kind = kind, Key = key, FirstSeen = now, HdtVersion = hdtVersion ?? "" };
				Entries.Add(e);
			}
			e.LastSeen = now;
			e.Count++;
			return e;
		}

		public static void AddDetail(MechEntry e, string detail)
		{
			if(string.IsNullOrWhiteSpace(detail) || e.Details.Contains(detail))
				return;
			e.Details.Add(detail);
			if(e.Details.Count > MaxDetails)
				e.Details.RemoveAt(0);
		}

		public static void AddExample(MechEntry e, CardExample c)
		{
			if(c == null || e.Examples.Any(x => x.CardId == c.CardId) || e.Examples.Count >= MaxExamples)
				return;
			e.Examples.Add(c);
		}

		/// <summary>對局結束：本場出現過的項目，累計出現場數與勝場（用來看這個機制對勝率的影響）。</summary>
		public void GameEnded(IEnumerable<(string kind, string key)> seen, bool won)
		{
			foreach(var (kind, key) in seen.Distinct())
			{
				var e = Entries.FirstOrDefault(x => x.Kind == kind && x.Key == key);
				if(e == null) continue;
				e.GamesSeen++;
				if(won) e.Wins++;
			}
		}

		// ───────── 存檔 ─────────
		private static readonly DataContractJsonSerializer Ser = new DataContractJsonSerializer(typeof(MechReport));

		public string ToJson()
		{
			using var ms = new MemoryStream();
			Ser.WriteObject(ms, this);
			return Encoding.UTF8.GetString(ms.ToArray());
		}

		public static MechReport FromJson(string json)
		{
			using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
			var r = (MechReport)Ser.ReadObject(ms);
			r.Entries ??= new List<MechEntry>();
			return r;
		}

		public static MechReport Load(string path)
		{
			try { return File.Exists(path) ? FromJson(File.ReadAllText(path, Encoding.UTF8)) : new MechReport(); }
			catch(Exception) { return new MechReport(); }
		}

		public void Save(string path)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, ToJson(), Encoding.UTF8);
		}

		// ───────── 給 Claude 看的 Markdown 報告 ─────────
		private static readonly string[] KindOrder = { MechKind.Stuck, MechKind.PlayFail, MechKind.Keyword, MechKind.Reader, MechKind.Mode, MechKind.HdtUpdate };

		public static string SuggestionFor(MechEntry e)
		{
			switch(e.Kind)
			{
				case MechKind.Stuck:
					return "新的選擇畫面或互動：參考 HDT 對應的 Watcher（Hearthstone/Watchers.cs）與 Utility/RegionDrawer/RegionDrawer.cs 的版面，" +
					       "在 Bot/AutoPlayer.cs 加處理（仿 HandleAssemble：讀記憶體狀態 → 點選 → 驗證），位置寫進 Bot/Snapshotter.cs 或獨立檔案。";
				case MechKind.PlayFail:
					return "AI 拖出去卻沒成功：多半是需要目標或有特殊打出方式。檢查 Bot/CardText.cs 的目標判斷（TargetKind）、" +
					       "Bot/AutoPlayer.cs PlayCard 的戰吼目標流程；若是新機制，在 Bot/Search.cs 的 PlayCard 模擬效果。";
				case MechKind.Keyword:
					return "新關鍵字：在 Bot/CardText.cs 判斷是否需要目標／是否全場效果，在 Bot/Search.cs 加入模擬效果與評分（Evaluate），" +
					       "並在 tests/HsHelper.Tests/Program.cs 加情境測試確認 AI 會正確使用。看「出現時勝率」判斷優先順序。";
				case MechKind.Reader:
					return "HearthMirror 新增讀取功能：查最新 HDT 原始碼中誰呼叫它（Watchers.cs 的 Provider），通常對應新畫面。" +
					       "若自動出牌會遇到該畫面，用反射呼叫（仿 Bot/Assemble.cs 的 Read）避免舊版 HDT 不相容。";
				case MechKind.Mode:
					return "新玩法：確認 Bot/Snapshotter.cs 是否應支援（目前排除酒館戰棋、傭兵），以及 Bot/AutoPlayer.cs HandleMenus 的開始按鈕／場景判斷。";
				default:
					return "確認 HDT 更新內容，必要時更新 scripts/fetch-hdt-refs.ps1 參考檔與相容性。";
			}
		}

		public string ToMarkdown(string pluginVersion, string hdtVersion, string generatedAt = null)
		{
			generatedAt ??= DateTime.Now.ToString("yyyy-MM-dd HH:mm");
			var sb = new StringBuilder();
			sb.AppendLine("# 爐石輔助：新機制報告");
			sb.AppendLine();
			sb.AppendLine($"- 產生時間：{generatedAt}");
			sb.AppendLine($"- 外掛版本：{pluginVersion}　HDT 版本：{hdtVersion}");
			sb.AppendLine($"- 專案：GitHub `rcj811/hdt-hs-helper`（HDT 外掛，C#）");
			sb.AppendLine();
			sb.AppendLine("## 給 Claude 的說明");
			sb.AppendLine();
			sb.AppendLine("這份報告由外掛自動整理，列出自動出牌遇到的新機制與問題。請依「優先處理」順序修改，目標是**不會卡住**並**提高勝率**：");
			sb.AppendLine();
			sb.AppendLine("1. 先處理「卡住的畫面」與「AI 打不出來的卡」（會直接浪費回合）");
			sb.AppendLine("2. 再處理出現場數多、勝率低的「新卡牌關鍵字」（影響勝率）");
			sb.AppendLine("3. 每項修改都在 `tests/HsHelper.Tests` 加情境測試；GitHub Actions 測試通過才發布");
			sb.AppendLine("4. 處理完的項目，在 report-data.json 把 `Resolved` 設為 true（下次報告就不會再列為待處理）");
			sb.AppendLine();

			var pending = Entries.Where(e => !e.Resolved).ToList();
			sb.AppendLine("## 摘要");
			sb.AppendLine();
			sb.AppendLine("| 類型 | 待處理 | 已處理 |");
			sb.AppendLine("|---|---|---|");
			foreach(var k in KindOrder)
			{
				var all = Entries.Where(e => e.Kind == k).ToList();
				if(all.Count > 0)
					sb.AppendLine($"| {k} | {all.Count(e => !e.Resolved)} | {all.Count(e => e.Resolved)} |");
			}
			sb.AppendLine();

			var rank = pending
				.OrderBy(e => Array.IndexOf(KindOrder, e.Kind) < 0 ? 99 : Array.IndexOf(KindOrder, e.Kind))
				.ThenByDescending(e => e.GamesSeen > 0 ? (e.GamesSeen - e.Wins) : e.Count)
				.ToList();
			if(rank.Count == 0)
			{
				sb.AppendLine("目前沒有待處理的新機制。");
				return sb.ToString();
			}

			sb.AppendLine("## 待處理項目（依優先順序）");
			var n = 0;
			foreach(var e in rank)
			{
				n++;
				sb.AppendLine();
				sb.AppendLine($"### {n}. 【{e.Kind}】{e.Key}");
				sb.AppendLine();
				sb.AppendLine($"- 第一次：{e.FirstSeen}　最近：{e.LastSeen}　出現 {e.Count} 次（當時 HDT {e.HdtVersion}）");
				if(e.GamesSeen > 0)
					sb.AppendLine($"- 出現的對局：{e.GamesSeen} 場，勝 {e.Wins} 場（勝率 {e.Wins * 100.0 / e.GamesSeen:F0}%）");
				foreach(var d in e.Details)
					sb.AppendLine($"- {d}");
				if(e.Examples.Count > 0)
				{
					sb.AppendLine();
					sb.AppendLine("| 卡牌 | 類型 | 費用 | 攻/血 | 英文敘述 | 遊戲標籤 | AI 的理解 |");
					sb.AppendLine("|---|---|---|---|---|---|---|");
					foreach(var c in e.Examples)
						sb.AppendLine($"| {Esc(c.NameZh)}／{Esc(c.NameEn)}（`{c.CardId}`） | {c.Type} | {c.Cost} | {c.Atk}/{c.Hp} | {Esc(c.TextEn)} | {Esc(c.Tags)} | {Esc(c.AiReading)} |");
					sb.AppendLine();
				}
				if(e.Screenshots.Count > 0)
					sb.AppendLine($"- 截圖：{string.Join("、", e.Screenshots.Select(s => $"`shots/{s}`"))}");
				sb.AppendLine($"- 建議處理：{SuggestionFor(e)}");
			}
			return sb.ToString();
		}

		private static string Esc(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
	}
}
