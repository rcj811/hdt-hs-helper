using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
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
			[DataMember(Order = 6)] public string PluginVersion = "";
		}

		public static readonly string KnownPath = Path.Combine(AutoPlayer.DataDir, "known-mechanics.json");
		public static readonly string NoticePath = Path.Combine(AutoPlayer.DataDir, "notices.log");

		public static readonly string ReportDir = Path.Combine(AutoPlayer.DataDir, "reports");
		public static readonly string ReportDataPath = Path.Combine(ReportDir, "report-data.json");
		public static readonly string ShotsDir = Path.Combine(ReportDir, "shots");
		private static MechReport _report;
		private static readonly HashSet<(string, string)> _gameSeen = new HashSet<(string, string)>();

		private static MechReport Report_ => _report ??= MechReport.Load(ReportDataPath);
		private static string HdtVersion => typeof(ApiCore).Assembly.GetName().Version?.ToString() ?? "?";
		public static string PluginVersion => typeof(MechanicMonitor).Assembly.GetName().Version?.ToString() ?? "?";

		private static void SaveReport()
		{
			try { Report_.Save(ReportDataPath); }
			catch(Exception ex) { Log.Warn("report save failed: " + ex.Message); }
		}

		// 這些標籤每張卡都有，報告中略過
		private static readonly HashSet<GameTag> NoisyTags = new HashSet<GameTag>
		{
			GameTag.ZONE, GameTag.CONTROLLER, GameTag.ENTITY_ID, GameTag.ZONE_POSITION, GameTag.CREATOR, GameTag.PREMIUM,
			GameTag.CARDTYPE, GameTag.COST, GameTag.ATK, GameTag.HEALTH, GameTag.RARITY, GameTag.CLASS, GameTag.CARD_SET,
			GameTag.FACTION, GameTag.COLLECTIBLE, GameTag.NUM_TURNS_IN_PLAY, GameTag.JUST_PLAYED, GameTag.EXHAUSTED,
			GameTag.CREATOR_DBID, GameTag.TAG_LAST_KNOWN_COST_IN_HAND, GameTag.LAUNCHPAD
		};

		/// <summary>把一張卡整理成報告用的範例（UI 執行緒）。</summary>
		public static CardExample Example(Entity e)
		{
			if(e == null)
				return null;
			var raw = e.Card?.Data?.GetLocText(Locale.enUS) ?? "";
			var tags = "";
			try
			{
				tags = string.Join("; ", e.Tags.Where(t => t.Value != 0 && !NoisyTags.Contains(t.Key))
					.Take(25).Select(t => $"{t.Key}={t.Value}"));
			}
			catch(Exception) { }
			var probe = new Ent { Text = raw, Kind = e.IsMinion ? Kind.Minion : e.IsSpell ? Kind.Spell : Kind.Other };
			CardText.Classify(probe);
			return new CardExample
			{
				CardId = e.CardId ?? "", NameZh = e.Card?.LocalizedName ?? "", NameEn = e.Card?.Data?.GetLocName(Locale.enUS) ?? "",
				Type = e.Card?.Type ?? "", Cost = e.GetTag(GameTag.COST), Atk = e.GetTag(GameTag.ATK), Hp = e.GetTag(GameTag.HEALTH),
				TextEn = raw, Tags = tags,
				AiReading = $"目標={probe.Target}，傷害={probe.Damage}，全場={(probe.AoE ? "是" : "否")}"
			};
		}

		private static Entity FindEntity(int id)
		{
			var game = ApiCore.Game;
			return game != null && game.Entities.TryGetValue(id, out var e) ? e : null;
		}

		/// <summary>擷取爐石視窗畫面（縮到 1280 寬），回傳檔名。</summary>
		private static string Screenshot(string tag)
		{
			try
			{
				var m = Menu.Read();
				if(!m.HasWindow)
					return null;
				Directory.CreateDirectory(ShotsDir);
				var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{tag}.png";
				using var bmp = new System.Drawing.Bitmap((int)m.Width, (int)m.Height);
				using(var g = System.Drawing.Graphics.FromImage(bmp))
					g.CopyFromScreen((int)m.Left, (int)m.Top, 0, 0, bmp.Size);
				var scale = Math.Min(1.0, 1280.0 / bmp.Width);
				using var small = new System.Drawing.Bitmap(bmp, new System.Drawing.Size((int)(bmp.Width * scale), (int)(bmp.Height * scale)));
				small.Save(Path.Combine(ShotsDir, name), System.Drawing.Imaging.ImageFormat.Png);
				return name;
			}
			catch(Exception ex)
			{
				Log.Warn("screenshot failed: " + ex.Message);
				return null;
			}
		}

		private static void AddShot(MechEntry e, string tag)
		{
			if(e.Screenshots.Count >= MechReport.MaxShots)
				return;
			var shot = Screenshot(tag);
			if(shot != null)
				e.Screenshots.Add(shot);
		}

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
			"armor", "attack", "health", "legendary", "hero power", "prepare", "passive", "battlecry", "deathrattle", "spellburst", "combo", "objective", "location", "silenced", "infused", "bonus effect"
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
				if(!firstRun)
				{
					if(k.HdtVersion != hdtVersion)
						Report_.Touch(MechKind.HdtUpdate, $"{k.HdtVersion} → {hdtVersion}", hdtVersion);
					foreach(var mName in newMethods)
					{
						var e = Report_.Touch(MechKind.Reader, Pretty(mName), hdtVersion);
						MechReport.AddDetail(e, "HearthMirror 方法：" + Describe(mName));
					}
					if(tagCount > k.GameTagCount && k.GameTagCount > 0)
					{
						var e = Report_.Touch(MechKind.HdtUpdate, $"卡牌資料庫新增 {tagCount - k.GameTagCount} 個遊戲標籤", hdtVersion);
						MechReport.AddDetail(e, "新標籤（列舉尾端）：" + string.Join(", ", Enum.GetNames(typeof(GameTag)).Skip(k.GameTagCount).Take(30)));
					}
					SaveReport();
				}
				// 外掛更新後第一次啟動：舊版記錄的「出牌／攻擊失敗」多是舊版的拖曳或判斷問題（已在新版修正），標記已處理
				if(!k.Keywords.Contains("hero power")) k.Keywords.Add("hero power");
				var pv = PluginVersion;
				if(!firstRun && k.PluginVersion != pv)
				{
					var migrated = 0;
					foreach(var e in Report_.Entries.Where(e => !e.Resolved && (e.Kind == MechKind.PlayFail
						|| (e.Kind == MechKind.Keyword && BuiltInKeywords.Contains(Normalize(e.Key))))))
					{
						e.Resolved = true;
						migrated++;
					}
					if(migrated > 0)
					{
						SaveReport();
						messages.Add($"外掛已更新到 {pv}：舊版記錄的 {migrated} 項已在新版修正，標記為已處理（再發生會重新記錄）");
					}
				}
				k.PluginVersion = pv;

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

		/// <summary>列出新讀取功能的回傳型別與欄位，方便判斷用途。</summary>
		private static string Describe(string method)
		{
			try
			{
				var client = typeof(HearthMirror.Reflection).GetProperty("Client")?.GetValue(null);
				var mi = client?.GetType().GetMethods().FirstOrDefault(x => x.Name == method);
				if(mi == null) return method;
				var rt = Nullable.GetUnderlyingType(mi.ReturnType) ?? mi.ReturnType;
				var members = rt.GetProperties().Select(p => $"{p.Name}:{p.PropertyType.Name}")
					.Concat(rt.GetFields().Select(f => $"{f.Name}:{f.FieldType.Name}")).Take(20);
				var args = string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
				return $"{method}({args}) → {rt.Name} {{ {string.Join(", ", members)} }}";
			}
			catch(Exception) { return method; }
		}

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
				var reportChanged = false;

				var gameType = $"{game.CurrentGameType}/{game.CurrentFormatType}";
				if(!k.GameTypes.Contains(gameType))
				{
					k.GameTypes.Add(gameType);
					changed = true;
					var supported = !game.IsBattlegroundsMatch && !game.IsMercenariesMatch;
					if(k.GameTypes.Count > 1) // 第一次記錄的不通知
					{
						Report($"第一次遇到的玩法：{gameType}，自動出牌{(supported ? "支援" : "不支援（只會顯示面板）")}");
						var e = Report_.Touch(MechKind.Mode, gameType, HdtVersion);
						MechReport.AddDetail(e, $"自動出牌：{(supported ? "支援" : "不支援")}；場景 {game.CurrentMode}");
						AddShot(e, "mode");
						SaveReport();
					}
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
							if(kw.Length < 3)
								continue;
							if(!k.Keywords.Contains(kw))
							{
								k.Keywords.Add(kw);
								changed = true;
								Report($"第一次看到的卡牌關鍵字：「{m.Groups[1].Value.Trim()}」（{e.Card?.LocalizedName}）— AI 先用費用估值處理，已記入新機制報告");
								var entry = Report_.Touch(MechKind.Keyword, kw, HdtVersion);
								MechReport.AddExample(entry, Example(e));
								AddShot(entry, "keyword");
								reportChanged = true;
							}
							else
							{
								// 已記錄的新關鍵字：累計範例卡與出現的對局
								var entry = Report_.Entries.FirstOrDefault(x => x.Kind == MechKind.Keyword && x.Key == kw);
								if(entry != null)
								{
									if(_gameSeen.Add((entry.Kind, entry.Key)))
										reportChanged = true;
									if(entry.Examples.All(x => x.CardId != e.CardId) && entry.Examples.Count < MechReport.MaxExamples)
									{
										MechReport.AddExample(entry, Example(e));
										reportChanged = true;
									}
								}
							}
						}
					}
				}
				catch(Exception)
				{
					// 集合在讀取中被修改：下次再掃
				}
				if(changed)
					Save();
				if(reportChanged)
					SaveReport();
			}
		}

		private static string Normalize(string kw)
		{
			kw = Regex.Replace(kw, @"[\d\$#:：\+\-/().,!?。，！]+", " ").Trim().ToLowerInvariant();
			return Regex.Replace(kw, @"\s+", " ");
		}

		/// <summary>自動出牌遇到處理不了的畫面時呼叫（同一個情況只通知一次）。</summary>
		private static readonly HashSet<string> _reported = new HashSet<string>();

		/// <summary>從背景執行緒呼叫：在 UI 執行緒整理卡牌資料。</summary>
		private static T OnUi<T>(Func<T> f)
		{
			try { return System.Windows.Application.Current.Dispatcher.Invoke(f); }
			catch(Exception) { return default; }
		}

		public static void ReportStuck(string what, IEnumerable<int> entityIds = null, string detail = null)
		{
			var ids = entityIds?.ToList() ?? new List<int>();
			var examples = OnUi(() => ids.Select(id => Example(FindEntity(id))).Where(x => x != null).ToList()) ?? new List<CardExample>();
			lock(Lock)
			{
				var e = Report_.Touch(MechKind.Stuck, what, HdtVersion);
				MechReport.AddDetail(e, detail);
				foreach(var ex in examples)
					MechReport.AddExample(e, ex);
				AddShot(e, "stuck");
				_gameSeen.Add((e.Kind, e.Key));
				SaveReport();
				if(!_reported.Add(what))
					return;
			}
			Report($"自動出牌處理不了：{what}（已記入新機制報告，可從選單匯出給 Claude）");
		}

		/// <summary>出牌／攻擊／英雄技能沒成功：記錄卡牌資料（同一張卡累計次數）。</summary>
		public static void PlayFailure(int entityId, string what, string reason)
		{
			var ex = OnUi(() => Example(FindEntity(entityId)));
			lock(Lock)
			{
				var key = ex != null && !string.IsNullOrEmpty(ex.CardId) ? $"{what}（{ex.CardId}）" : what;
				var e = Report_.Touch(MechKind.PlayFail, key, HdtVersion);
				MechReport.AddDetail(e, reason);
				MechReport.AddExample(e, ex);
				if(e.Count <= 2)
					AddShot(e, "playfail");
				_gameSeen.Add((e.Kind, e.Key));
				SaveReport();
			}
		}

		public static void GameEnded(bool won)
		{
			lock(Lock)
			{
				if(_gameSeen.Count > 0)
				{
					Report_.GameEnded(_gameSeen, won);
					SaveReport();
				}
				_gameSeen.Clear();
			}
		}

		public static int PendingCount
		{
			get { lock(Lock) return Report_.Entries.Count(e => !e.Resolved && e.Kind != MechKind.HdtUpdate); }
		}

		/// <summary>匯出給 Claude 的報告包：report.md、report-data.json、notices.log、截圖、相關 HDT log。</summary>
		public static void ExportZip(string zipPath)
		{
			string md, json;
			lock(Lock)
			{
				md = Report_.ToMarkdown(PluginVersion, HdtVersion);
				json = Report_.ToJson();
			}
			var tmp = Path.Combine(Path.GetTempPath(), "hshelper-report-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tmp);
			try
			{
				File.WriteAllText(Path.Combine(tmp, "report.md"), md, Encoding.UTF8);
				File.WriteAllText(Path.Combine(tmp, "report-data.json"), json, Encoding.UTF8);
				if(File.Exists(NoticePath))
					File.Copy(NoticePath, Path.Combine(tmp, "notices.log"));
				if(Directory.Exists(ShotsDir))
				{
					var shots = Path.Combine(tmp, "shots");
					Directory.CreateDirectory(shots);
					foreach(var f in Directory.GetFiles(ShotsDir, "*.png"))
						File.Copy(f, Path.Combine(shots, Path.GetFileName(f)));
				}
				File.WriteAllText(Path.Combine(tmp, "hdt-log-excerpt.txt"), HdtLogExcerpt(), Encoding.UTF8);
				try
				{
					// 實戰觀察學到的卡牌效果（給 Claude 檢查 AI 學得對不對）
					var db = LearningDb.Load(AutoPlayer.DbPath);
					var sb = new StringBuilder("# 實戰學到的卡牌效果\n\n| 卡牌 | CardId | 學到的效果 | 觀察 | 試用無效 |\n|---|---|---|---|---|\n");
					foreach(var l in db.Lessons.OrderByDescending(x => x.Tries))
						sb.Append($"| {l.Name} | {l.CardId} | {l.Summary} | {l.Tries} | {l.NoEffect} |\n");
					File.WriteAllText(Path.Combine(tmp, "card-lessons.md"), sb.ToString(), Encoding.UTF8);
				}
				catch(Exception) { /* 讀不到就算了 */ }
				if(File.Exists(zipPath))
					File.Delete(zipPath);
				System.IO.Compression.ZipFile.CreateFromDirectory(tmp, zipPath);
			}
			finally
			{
				try { Directory.Delete(tmp, true); } catch(Exception) { }
			}
		}

		/// <summary>HDT log 中跟自動出牌有關的最後 400 行。</summary>
		private static string HdtLogExcerpt()
		{
			try
			{
				var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "Logs");
				var file = new DirectoryInfo(dir).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
				if(file == null)
					return "(找不到 HDT log)";
				string[] lines;
				using(var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				using(var sr = new StreamReader(fs))
					lines = sr.ReadToEnd().Split('\n');
				var keys = new[] { "HsHelper", "Action:", "Plan:", "Notice:", "Choice:", "Assemble", "Deck:", "Learning", "failed", "Play failed", "bounced", "Turn:", "Cards:", "Lesson:", "Exception" };
				return string.Join("\n", lines.Where(l => keys.Any(l.Contains)).Reverse().Take(400).Reverse());
			}
			catch(Exception ex)
			{
				return "(讀取 HDT log 失敗：" + ex.Message + ")";
			}
		}
	}
}
