using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Utility.Logging;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper.Bot
{
	/// <summary>
	/// 新機制偵測：
	///  1. HDT 更新：比對 HDT 版本、HearthMirror 的遊戲讀取功能（Get…）、卡牌資料庫的遊戲標籤數量
	///  2. 新玩法：第一次遇到的遊戲類型／格式
	///  3. 新卡牌關鍵字：卡牌敘述中第一次出現的粗體關鍵字；以及處理不了的選擇畫面
	/// 通知會跳在畫面中央，並記錄在 %AppData%\HsHelper\notices.log。
	/// </summary>
	public static class MechanicMonitor
	{
		[DataContract]
		public class Known
		{
			[DataMember(Order = 1)] public string HdtVersion = "";
			[DataMember(Order = 2)] public int GameTagCount;
			[DataMember(Order = 3)] public List<string> MirrorMethods = new List<string>();
			[DataMember(Order = 4)] public List<string> GameTypes = new List<string>();
			[DataMember(Order = 5)] public List<string> Keywords = new List<string>();
		}

		public static readonly string KnownPath = Path.Combine(AutoPlayer.DataDir, "known-mechanics.json");
		public static readonly string NoticePath = Path.Combine(AutoPlayer.DataDir, "notices.log");

		/// <summary>有新通知時觸發（外掛用它顯示畫面中央的提示）。</summary>
		public static event Action<string> Notice;

		private static Known _known;
		private static readonly object Lock = new object();
		private static readonly Regex Bold = new Regex(@"<b>([^<]{2,40})</b>", RegexOptions.Compiled);

		// AI 已知的關鍵字（第一次執行時視為已知，不通知）
		private static readonly string[] BuiltInKeywords =
		{
			"taunt", "charge", "rush", "divine shield", "windfury", "mega-windfury", "lifesteal", "poisonous", "venomous",
			"stealth", "battlecry", "deathrattle", "discover", "secret", "choose one", "combo", "overload", "freeze", "frozen",
			"silence", "spell damage", "inspire", "adapt", "echo", "magnetic", "reborn", "twinspell", "outcast", "spellburst",
			"frenzy", "honorable kill", "tradeable", "corrupt", "dormant", "infuse", "manathirst", "overheal", "forge",
			"excavate", "titan", "colossal", "quickdraw", "dredge", "finale", "elusive", "immune", "counter", "recruit",
			"lackey", "invoke", "sidequest", "questline", "quest", "start of game", "corpse", "corpses", "miniaturize",
			"gigantify", "tourist", "starship", "imbue", "kindred", "herald", "assemble", "dark gift", "outcast",
			"armor", "attack", "health", "legendary", "spellburst", "combo", "objective", "location", "silenced"
		};

		private static Known Load()
		{
			if(_known != null)
				return _known;
			try
			{
				if(File.Exists(KnownPath))
				{
					using var fs = File.OpenRead(KnownPath);
					_known = (Known)new DataContractJsonSerializer(typeof(Known)).ReadObject(fs);
				}
			}
			catch(Exception ex)
			{
				Log.Warn("known-mechanics.json read failed: " + ex.Message);
			}
			return _known ??= new Known();
		}

		private static void Save()
		{
			try
			{
				Directory.CreateDirectory(AutoPlayer.DataDir);
				using var ms = new MemoryStream();
				new DataContractJsonSerializer(typeof(Known)).WriteObject(ms, _known);
				File.WriteAllBytes(KnownPath, ms.ToArray());
			}
			catch(Exception ex)
			{
				Log.Warn("known-mechanics.json save failed: " + ex.Message);
			}
		}

		public static void Report(string message)
		{
			try
			{
				Directory.CreateDirectory(AutoPlayer.DataDir);
				File.AppendAllText(NoticePath, $"{DateTime.Now:yyyy-MM-dd HH:mm}  {message}{Environment.NewLine}", Encoding.UTF8);
			}
			catch(Exception)
			{
				// 忽略
			}
			Log.Info("Notice: " + message);
			Notice?.Invoke(message);
		}

		// ───────── 1. 啟動時：HDT 更新偵測 ─────────
		public static void CheckOnStartup()
		{
			lock(Lock)
			{
				var k = Load();
				var firstRun = string.IsNullOrEmpty(k.HdtVersion);

				var hdtVersion = typeof(ApiCore).Assembly.GetName().Version?.ToString() ?? "?";
				var tagCount = Enum.GetNames(typeof(GameTag)).Length;
				var methods = MirrorMethods();

				var newMethods = methods.Except(k.MirrorMethods).ToList();
				var messages = new List<string>();
				if(firstRun)
				{
					foreach(var kw in BuiltInKeywords)
						if(!k.Keywords.Contains(kw)) k.Keywords.Add(kw);
					messages.Add($"新機制偵測已啟用：已記錄 HDT {hdtVersion} 的 {methods.Count} 項遊戲讀取功能");
				}
				else
				{
					if(k.HdtVersion != hdtVersion)
						messages.Add($"HDT 已更新：{k.HdtVersion} → {hdtVersion}");
					if(newMethods.Count > 0)
						messages.Add($"HDT 新增遊戲讀取功能（可能是新機制／新畫面）：{string.Join("、", newMethods.Select(Pretty))}");
					if(tagCount > k.GameTagCount && k.GameTagCount > 0)
						messages.Add($"卡牌資料庫新增 {tagCount - k.GameTagCount} 個遊戲標籤（可能有新卡牌機制）");
				}
				k.HdtVersion = hdtVersion;
				k.GameTagCount = tagCount;
				k.MirrorMethods = methods;
				Save();
				foreach(var m in messages)
					Report(m);
			}
		}

		private static List<string> MirrorMethods()
		{
			try
			{
				var client = typeof(HearthMirror.Reflection).GetProperty("Client")?.GetValue(null);
				return client?.GetType().GetMethods()
					.Where(m => m.Name.StartsWith("Get") && m.DeclaringType != typeof(object))
					.Select(m => m.Name).Distinct().OrderBy(n => n).ToList() ?? new List<string>();
			}
			catch(Exception)
			{
				return new List<string>();
			}
		}

		// GetAssembleState → Assemble（組合）
		private static readonly Dictionary<string, string> KnownNames = new Dictionary<string, string>
		{
			["Assemble"] = "組合", ["Discover"] = "發現", ["Choices"] = "選擇", ["Mulligan"] = "換牌", ["Bacon"] = "酒館戰棋",
			["Mercenaries"] = "傭兵", ["Arena"] = "競技場", ["DeckPicker"] = "選套牌", ["Pack"] = "卡包", ["Reward"] = "獎勵",
			["Starship"] = "星艦", ["Forge"] = "鍛造", ["Trinket"] = "飾品", ["Anomaly"] = "異象", ["Quest"] = "任務"
		};

		private static string Pretty(string method)
		{
			var name = method.StartsWith("Get") ? method.Substring(3) : method;
			foreach(var kv in KnownNames)
				if(name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
					return $"{name}（{kv.Value}）";
			return name;
		}

		// ───────── 2＋3. 對局中：新玩法、新關鍵字（UI 執行緒呼叫）─────────
		private static DateTime _lastScan = DateTime.MinValue;

		public static void CheckInGame()
		{
			if((DateTime.Now - _lastScan).TotalSeconds < 3)
				return;
			_lastScan = DateTime.Now;
			var game = ApiCore.Game;
			if(game == null || !game.IsRunning || game.IsInMenu)
				return;
			lock(Lock)
			{
				var k = Load();
				var changed = false;

				var gameType = $"{game.CurrentGameType}/{game.CurrentFormatType}";
				if(!k.GameTypes.Contains(gameType))
				{
					k.GameTypes.Add(gameType);
					changed = true;
					var supported = !game.IsBattlegroundsMatch && !game.IsMercenariesMatch;
					if(k.GameTypes.Count > 1) // 第一次記錄的不通知
						Report($"第一次遇到的玩法：{gameType}，自動出牌{(supported ? "支援" : "不支援（只會顯示面板）")}");
				}

				try
				{
					var entities = game.Player.Hand.Concat(game.Player.Board).Concat(game.Opponent.Board);
					foreach(var e in entities)
					{
						var text = e.Card?.Data?.GetLocText(Locale.enUS); // 原始敘述（保留 <b> 粗體關鍵字標記）
						if(string.IsNullOrEmpty(text))
							continue;
						foreach(Match m in Bold.Matches(text))
						{
							var kw = Normalize(m.Groups[1].Value);
							if(kw.Length < 3 || k.Keywords.Contains(kw))
								continue;
							k.Keywords.Add(kw);
							changed = true;
							Report($"第一次看到的卡牌關鍵字：「{m.Groups[1].Value.Trim()}」（{e.Card?.LocalizedName}）— AI 先用費用估值處理，有問題請回報");
						}
					}
				}
				catch(Exception)
				{
					// 集合在讀取中被修改：下次再掃
				}
				if(changed)
					Save();
			}
		}

		private static string Normalize(string kw)
		{
			kw = Regex.Replace(kw, @"[\d\$#:：\+\-/()]+", " ").Trim().ToLowerInvariant();
			return Regex.Replace(kw, @"\s+", " ");
		}

		/// <summary>自動出牌遇到處理不了的畫面時呼叫（同一個情況只通知一次）。</summary>
		private static readonly HashSet<string> _reported = new HashSet<string>();

		public static void ReportStuck(string what)
		{
			lock(Lock)
				if(!_reported.Add(what))
					return;
			Report($"自動出牌處理不了：{what}（請截圖回報，會在下一版支援）");
		}
	}
}
