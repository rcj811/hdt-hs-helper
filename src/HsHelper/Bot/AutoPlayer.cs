using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Hearthstone_Deck_Tracker.Utility.Logging;

namespace HsHelper.Bot
{
	/// <summary>
	/// 自動出牌主迴圈（背景執行緒）。
	/// 每一步：在 UI 執行緒讀狀態 → 決定一個動作 → 模擬滑鼠 → 等遊戲反應 → 重來。
	/// 失敗的動作會記在本回合的略過清單，避免卡住。
	/// </summary>
	public class AutoPlayer
	{
		private Thread _thread;
		private volatile bool _running;
		private readonly HashSet<int> _skip = new HashSet<int>();
		private int _skipTurn = -1;
		private bool _mulliganDone;
		private int _endTurnAttempts;

		public bool Running => _running;

		// ───────── 學習 ─────────
		public static readonly string DataDir = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HsHelper");
		public static readonly string DbPath = System.IO.Path.Combine(DataDir, "learning.json");
		public readonly LearningDb Db;
		public readonly object DbLock = new object();
		private readonly Weights _baseWeights = new Weights();
		private bool _gameActive, _gameRecorded;
		private Style _style;
		private int _oppTurns, _faceTaken, _maxBurst, _aoe;
		private int _endLife = -1, _endBoard = -1;
		private bool _wasMyTurn;
		public string StyleText { get; private set; } = "";
		private string _deckText = "";

		public AutoPlayer()
		{
			Db = LearningDb.Load(DbPath);
			// 內建的模擬器學習資料：逐筆比對 Id，資料庫沒有才加入
			try
			{
				using var stream = typeof(AutoPlayer).Assembly.GetManifestResourceStream("HsHelper.learning-seed.json");
				if(stream != null)
				{
					using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
					var seed = LearningDb.FromJson(reader.ReadToEnd());
					var (added, _) = Db.Merge(seed.Games);
					if(added > 0)
						Db.Save(DbPath);
					Log.Info($"Learning DB: {Db.Games.Count} records ({added} seed added)");
				}
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		public (int added, int skipped) Import(string path)
		{
			lock(DbLock)
			{
				var r = Db.ImportFile(path);
				Db.Save(DbPath);
				return r;
			}
		}

		public void Export(string path)
		{
			lock(DbLock)
				System.IO.File.WriteAllText(path, Db.ToJson(), System.Text.Encoding.UTF8);
		}

		/// <summary>對局中追蹤對手打法；我的回合開始時結算對手上一回合造成的傷害／清場。</summary>
		private void TrackGame(Snapshot s)
		{
			if(!_gameActive)
			{
				_gameActive = true;
				_gameRecorded = false;
				_oppTurns = _faceTaken = _maxBurst = _aoe = 0;
				_endLife = _endBoard = -1;
				_wasMyTurn = false;
				lock(DbLock)
					_style = Learner.ChooseStyle(Learner.Profile(Db, s.OppClass, s.OppCards));
			}
			var life = (s.MyHero?.Hp ?? 0) + (s.MyHero?.Armor ?? 0);
			var board = s.MyBoard.Count(m => m.Kind == Kind.Minion);
			if(s.MyTurn && !_wasMyTurn && _endLife >= 0)
			{
				var dmg = Math.Max(0, _endLife - life);
				_oppTurns++;
				_faceTaken += dmg;
				_maxBurst = Math.Max(_maxBurst, dmg);
				if(_endBoard - board >= 2) _aoe++;
			}
			if(s.MyTurn)
			{
				_endLife = life;
				_endBoard = board;
			}
			_wasMyTurn = s.MyTurn;
		}

		private void ApplyLearning(Snapshot s)
		{
			OpponentProfile p;
			lock(DbLock)
				p = Learner.Profile(Db, s.OppClass, s.OppCards,
					_oppTurns > 0 ? _faceTaken / (double)_oppTurns : -1,
					_oppTurns > 0 ? _aoe * 5.0 / _oppTurns : -1);
			// 職業卡牌模組：確認對手職業後，預測對手下回合可能的清場與爆發（v0.9.5）
			var threat = ClassThreat.For(s.OppClass, Math.Min(10, s.OppMana + 1), s.OppCards);
			Search.W = ClassThreat.Apply(Learner.Adjust(_baseWeights, _style, p), threat);
			StyleText = Learner.Describe(_style, p) + (string.IsNullOrEmpty(threat.Summary) ? "" : $"｜對手 {threat.Summary}")
			            + (string.IsNullOrEmpty(s.DeckName) ? "" : $"｜套牌 {s.DeckName}");
		}

		private void RecordGame(Snapshot s)
		{
			if(!_gameActive || _gameRecorded)
				return;
			_gameRecorded = true;
			_gameActive = false;
			var rec = new GameRecord
			{
				Source = "real", OppClass = s.OppClass ?? "", OppCards = s.OppCards ?? new List<string>(),
				Won = s.Won, Style = (int)_style, OppTurns = _oppTurns, FaceDmgTaken = _faceTaken,
				// HDT 在某些模式（例如練習）不會記錄本場套牌 → 用選套牌畫面讀到的
				DeckId = s.DeckId != 0 ? s.DeckId : _menuDeckId,
				DeckName = !string.IsNullOrEmpty(s.DeckName) ? s.DeckName : _menuDeckName,
				MaxBurst = _maxBurst, AoeEvents = _aoe
			};
			try
			{
				lock(DbLock)
				{
					Db.Add(rec);
					Db.Save(DbPath);
				}
				Log.Info($"Learning: recorded {(rec.Won ? "WIN" : "LOSS")} vs {rec.OppClass}, style {_style}");
				MechanicMonitor.GameEnded(rec.Won);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}
		/// <summary>對戰結束後自動點掉結算／獎勵畫面，並按「開始」進下一場。</summary>
		public bool AutoNext { get; set; } = true;
		/// <summary>開始下一場前，自動選預估勝率最高的套牌。</summary>
		public bool AutoDeck { get; set; } = true;
		private bool _deckReady;
		private readonly DeckNavigator _navigator = new DeckNavigator();
		private long _menuDeckId;
		private string _menuDeckName = "";
		private int _deckClickTries;
		private DateTime _lastPlayClick = DateTime.MinValue;
		private int _playClicks;
		private bool _sawGameOver;
		public string Status { get; private set; } = "關閉（按 F8 開始）";

		public void Toggle()
		{
			if(_running) Stop();
			else Start();
		}

		public void Start()
		{
			if(_running)
				return;
			_running = true;
			Status = "啟動中…";
			_thread = new Thread(Loop) { IsBackground = true, Name = "HsHelper AutoPlayer" };
			_thread.Start();
			Log.Info("AutoPlayer started");
		}

		public void Stop()
		{
			_running = false;
			Status = "關閉（按 F8 開始）";
			Log.Info("AutoPlayer stopped");
		}

		private static Snapshot Read()
		{
			try
			{
				return Application.Current.Dispatcher.Invoke(new Func<Snapshot>(Snapshotter.Take));
			}
			catch(Exception ex)
			{
				Log.Warn("Snapshot failed: " + ex.Message);
				return new Snapshot();
			}
		}

		private void Loop()
		{
			while(_running)
			{
				try
				{
					Step();
				}
				catch(Exception ex)
				{
					Log.Error(ex);
					Status = "發生錯誤，重試中…";
					Thread.Sleep(1500);
				}
			}
		}

		private void Step()
		{
			var s = Read();
			if(!Input.HearthstoneInForeground())
			{
				Status = "暫停：請把爐石視窗切到最前面";
				Thread.Sleep(700);
				return;
			}
			if(!s.InGame || s.GameOver)
			{
				_mulliganDone = false;
				_skipTurn = -1;
				if(s.GameOver)
				{
					_sawGameOver = true;
					RecordGame(s);
				}
				else if(!s.InGame && _gameActive && _sawGameOver == false)
					_gameActive = false; // 對局中途離開（例如投降後才開自動）：不記錄
				HandleMenus(s.GameOver);
				return;
			}
			_playClicks = 0;
			_sawGameOver = false;
			_deckReady = false;
			_deckClickTries = 0;
			TrackGame(s);

			if(s.MulliganInput)
			{
				if(!_mulliganDone)
					DoMulligan(s);
				else
					RetryMulliganConfirm(s);
				return;
			}
			_mulliganDone = true;

			if(!s.MyTurn || !s.MainAction)
			{
				Status = "等待我的回合";
				_endTurnAttempts = 0;
				Thread.Sleep(600);
				return;
			}

			if(_skipTurn != s.Turn)
			{
				_skip.Clear();
				_playFails.Clear();
				_skipTurn = s.Turn;
				Input.Pause(800, 1200); // 回合開始動畫
				return;
			}

			// 「組合」選擇畫面（新機制，版面跟「發現」不同）要先處理
			var asm = Assemble.Read();
			if(asm.Showing)
			{
				HandleAssemble(asm);
				return;
			}
			_asmTarget = 0;

			if(s.Offered.Count > 0)
			{
				ChooseOffered(s);
				return;
			}

			if(NothingToDo(s))
			{
				if(!_idleSeen)
				{
					_idleSeen = true; // 再確認一次，避免剛好讀到舊資料
					Thread.Sleep(200);
					return;
				}
				_idleSeen = false;
				if(TryUnknown(s))
					return;
				Status = "沒有可出的牌和水晶，結束回合";
				Log.Info("Action: 結束回合（沒事可做）");
				Input.Pause(100, 250);
				Input.Click(s.EndTurn);
				_endTurnAttempts++;
				WaitUntil(x => !x.MyTurn, 5000);
				Input.Hover(s.Neutral);
				return;
			}
			_idleSeen = false;

			if(!Settled(s))
			{
				Status = "等待畫面穩定…";
				Thread.Sleep(250);
				return;
			}
			_unsettledSince = DateTime.MinValue;

			ApplyLearning(s);
			lock(DbLock)
				Lessons.Apply(s, Db.LessonMap);
			TrackCards(s);
			BotAction action;
			try
			{
				action = Search.BestFirstAction(s, _skip, out var plan)
				         ?? new BotAction { Type = ActionType.EndTurn, Describe = "結束回合" };
				if(!string.IsNullOrEmpty(plan))
					Log.Info("Plan: " + plan);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
				action = Planner.Next(s, _skip); // 搜尋出錯時退回單步規則
			}
			if(action.Type == ActionType.EndTurn && TryUnknown(s))
				return;
			Status = action.Describe;
			Log.Info("Action: " + action.Describe);
			Execute(s, action);
		}

		// ───────── 手牌／牌庫能力（v0.9.5）─────────
		public string CardsText { get; private set; } = "";
		private string _handSig = "";

		/// <summary>每抽一張、打出一張（手牌或牌庫變了）就重新統計能力，寫進 log 並顯示在面板。</summary>
		private void TrackCards(Snapshot s)
		{
			var sig = string.Join(",", s.Hand.Select(h => h.Id)) + "|" + s.Deck.Count;
			if(sig == _handSig)
				return;
			_handSig = sig;
			CardsText = CardSummary.Line(s);
			Log.Info("Cards: " + CardsText + "｜手牌：" + string.Join("、", s.Hand.Select(h =>
			{
				var tags = CardText.Tags(h);
				return tags.Count > 0 ? $"{h.Name}[{string.Join(" ", tags)}]" : h.Name;
			})));
		}

		// ───────── 實戰學習看不懂的牌（v0.9.4） ─────────
		/// <summary>回合要結束、還有剩水晶時，試出一張「目標看不懂」的牌，觀察效果記下來（每張最多試 3 次）。</summary>
		private bool TryUnknown(Snapshot s)
		{
			Dictionary<string, CardLesson> map;
			lock(DbLock)
				map = Db.LessonMap;
			Lessons.Apply(s, map);
			var c = s.Hand.FirstOrDefault(h => h.Kind == Kind.Spell && h.Cost <= s.Mana && !_skip.Contains(h.Id) && Lessons.WorthTrying(h, map));
			var hp = s.MyHeroPower;
			if(c == null && hp != null && !hp.Exhausted && hp.Cost <= s.Mana && !_skip.Contains(hp.Id) && Lessons.WorthTrying(hp, map))
				c = hp;
			if(c == null)
				return false;
			map.TryGetValue(c.CardId, out var l);
			var t = Lessons.TrialTarget(s, l);
			if(t == null)
				return false;
			_skip.Add(c.Id); // 每回合每張只試一次
			var a = new BotAction
			{
				Type = c == hp ? ActionType.HeroPower : ActionType.Play, Source = c, Target = t, Trial = true,
				Describe = $"試用看不懂的{(c == hp ? "英雄技能" : "牌")}「{c.Name}」→ {t.Name}（觀察效果、記下來）"
			};
			Status = a.Describe;
			Log.Info("Action: " + a.Describe);
			Execute(s, a);
			return true;
		}

		/// <summary>出牌／技能結算後，比對前後局面記下實際效果。</summary>
		private void ObserveEffect(Snapshot before, Ent c, Ent target)
		{
			try
			{
				if(c == null || string.IsNullOrEmpty(c.CardId) || c.IsCoin || c.ManaGain > 0)
					return;
				var observe = c.Kind == Kind.Spell || c.Kind == Kind.HeroPower || c == before.MyHeroPower
				              || (c.Kind == Kind.Minion && (c.Text ?? "").IndexOf("battlecry", StringComparison.OrdinalIgnoreCase) >= 0);
				if(!observe)
					return;
				Thread.Sleep(900); // 等效果動畫與 HDT 記錄
				var after = Read();
				if(!after.InGame || after.GameOver)
					return;
				var e = Lessons.Observe(before, after, c, target);
				bool learned;
				string summary;
				lock(DbLock)
				{
					var l = Db.Lesson(c.CardId, c.Name);
					learned = Lessons.Learn(l, e, Search.W);
					summary = l.Summary;
					Db.Save(DbPath);
				}
				Log.Info($"Lesson: {c.Name} → {summary}");
				if(learned)
					MechanicMonitor.Report($"實戰學會新卡：「{c.Name}」{summary}");
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		private void TrialFailed(Ent c, Ent target, Snapshot s)
		{
			lock(DbLock)
			{
				var l = Db.Lesson(c.CardId, c.Name);
				var enemy = s.OppBoard.Any(m => m.Id == target?.Id);
				if(enemy) l.EnemyTries++; else l.FriendlyTries++;
				l.NoEffect++;
				l.Summary = $"試用 {l.EnemyTries + l.FriendlyTries} 次都無法施放在{(enemy ? "敵方" : "我方")}目標上";
				Db.Save(DbPath);
			}
			Log.Info($"Trial failed: {c.Name} on {target?.Name}");
		}

		// ───────── 對戰結束／選單：自動下一場 ─────────
		private static MenuState ReadMenu()
		{
			try { return Application.Current.Dispatcher.Invoke(new Func<MenuState>(Menu.Read)); }
			catch(Exception) { return new MenuState(); }
		}

		private void HandleMenus(bool gameOver)
		{
			var m = ReadMenu();
			if(!AutoNext)
			{
				Status = gameOver ? "對戰結束（自動下一場已關閉）" : "等待對戰";
				Thread.Sleep(1000);
				return;
			}
			if(!m.HasWindow)
			{
				Thread.Sleep(1000);
				return;
			}

			// 還在對戰場景：勝利／失敗畫面、經驗條、獎勵 → 點擊以繼續
			if(m.Mode == Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode.GAMEPLAY && !_sawGameOver)
			{
				Status = "對戰載入中…";
				Thread.Sleep(1000);
				return;
			}
			if(gameOver || m.Mode == Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode.GAMEPLAY)
			{
				Status = "對戰結束：點擊以繼續／領取獎勵";
				Input.Pause(2500, 3500);
				Input.Click(m.ContinueText);
				Input.Pause(1500, 2200);
				return;
			}

			// 回到「對戰」或「練習」的選牌畫面：按「開始」
			var mode = m.Mode;
			var canQueue = mode == Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode.TOURNAMENT
			               || mode == Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode.ADVENTURE;
			if(!canQueue)
			{
				Status = "在其他畫面，請進入「對戰」或「練習」選好牌組";
				Thread.Sleep(1500);
				return;
			}
			if(m.InQueue)
			{
				Status = "配對中…";
				Thread.Sleep(1500);
				return;
			}
			// 自動選套牌：從「所有頁」挑預估勝率最高的，必要時翻頁，點選後讀記憶體確認
			if(AutoDeck && !_deckReady)
			{
				Status = "選套牌中…";
				Input.Pause(800, 1300); // 等選套牌畫面動畫
				var msg = _navigator.EnsureBest(Db, DbLock, out _, out var chosen);
				var now = DeckPicker.Read();
				var sel = now.Decks.FirstOrDefault(d => d.Id == now.Selected) ?? chosen;
				if(sel != null) { _menuDeckId = sel.Id; _menuDeckName = sel.Name; }
				_deckReady = true;
				_deckText = msg;
				Status = "已選套牌：" + msg;
				Log.Info("Deck: " + msg);
				Input.Pause(600, 1000);
			}

			// 每次按完至少等 6 秒（可能是先關掉獎勵視窗，或正在進入對戰）
			if((DateTime.Now - _lastPlayClick).TotalSeconds < 6)
			{
				Thread.Sleep(800);
				return;
			}
			if(_playClicks >= 6)
			{
				Status = "按了幾次「開始」都沒反應，請手動開始（或回報位置）";
				Thread.Sleep(2000);
				return;
			}
			Status = "自動開始下一場";
			Input.Pause(2000, 3000);
			Input.Click(m.PlayButton);
			_lastPlayClick = DateTime.Now;
			_playClicks++;
		}

		// ───────── 換牌 ─────────
		private void DoMulligan(Snapshot s)
		{
			Status = "換牌中";
			Input.Pause(2500, 3500); // 等換牌畫面動畫
			s = Read();
			if(!s.MulliganInput)
				return;
			var cards = s.Hand.Where(c => !c.IsCoin).ToList();
			for(var i = 0; i < cards.Count && i < s.MulliganCards.Count; i++)
			{
				if(!Planner.ShouldReplace(cards[i], cards, s.HasCoin))
					continue;
				Log.Info($"Mulligan: replace {cards[i].Name}");
				Input.Click(s.MulliganCards[i]);
				Input.Pause(700, 1100);
			}
			Input.Pause(900, 1400);
			Input.Click(s.MulliganConfirm[0]);
			_mulliganDone = true;
			_confirmTry = 1;
			Input.Pause(2500, 3500);
		}

		private int _confirmTry;
		private void RetryMulliganConfirm(Snapshot s)
		{
			// 第一個位置沒按到「確認」：輪流試其他位置（換牌畫面也有倒數，最後會自動確認）
			if(s.MulliganConfirm.Count == 0)
				return;
			Status = "換牌：按確認";
			Input.Click(s.MulliganConfirm[_confirmTry % s.MulliganConfirm.Count]);
			_confirmTry++;
			Input.Pause(2500, 3500);
		}

		// ───────── 組合 ─────────
		private int _asmTarget, _asmTries, _asmBtn;

		/// <summary>
		/// 組合畫面：先點一個選項（費用高的卡優先、下半效果優先），讀記憶體確認選中後按「組合」。
		/// 每一步都用記憶體狀態驗證；沒反應就換下一個位置，試太多次就停下來回報，不會亂點。
		/// </summary>
		private void HandleAssemble(Assemble.State asm)
		{
			if(asm.Target != _asmTarget)
			{
				_asmTarget = asm.Target;
				_asmTries = 0;
				_asmBtn = 0;
				Input.Pause(800, 1300); // 等畫面動畫
			}
			_asmTries++;
			if(_asmTries > 12)
			{
				Status = "組合畫面卡住了，請截圖回報";
				MechanicMonitor.ReportStuck("組合畫面", asm.Choices.Concat(new[] { asm.Target }),
					$"組合畫面試了 12 次：目前選擇 {asm.Selected}，選項 {string.Join(",", asm.Choices)}");
				Thread.Sleep(2000);
				return;
			}
			var opts = Application.Current.Dispatcher.Invoke(new Func<List<Assemble.Option>>(() => Assemble.Options(asm)));
			if(opts.Count == 0)
			{
				Status = "組合畫面：讀不到選項";
				Thread.Sleep(1000);
				return;
			}

			if(opts.All(o => o.EntityId != asm.Selected))
			{
				var ordered = opts.OrderByDescending(o => o.Cost).ThenByDescending(o => o.Half).ToList();
				var pick = ordered[(_asmTries - 1) % ordered.Count];
				Status = $"思考中…（組合：{pick.Name} {(pick.Half == 1 ? "上半" : "下半")}）";
				Input.Pause(500, 2001);
				Status = $"組合：選 {pick.Name}（{(pick.Half == 1 ? "上半" : "下半")}）";
				Log.Info($"Assemble: pick {pick.Name} half {pick.Half}");
				Input.Click(pick.Screen);
				Input.Pause(900, 1400);
				return;
			}

			var buttons = Application.Current.Dispatcher.Invoke(new Func<List<Point>>(Assemble.ButtonPoints));
			var b = buttons[_asmBtn % buttons.Count];
			_asmBtn++;
			Status = "組合：按「組合」";
			Log.Info("Assemble: press button");
			Input.Click(b);
			Input.Pause(1200, 1800);
		}

		// ───────── 發現／抉擇 ─────────
		private string _offerKey = "";
		private int _offerTries;

		/// <summary>發現／抉擇／黑暗之賜：選費用最高（同費選數值高）的；點了沒反應就輪流試其他張，試太多次就通知。</summary>
		private void ChooseOffered(Snapshot s)
		{
			var key = string.Join(",", s.Offered.Select(o => o.Id));
			if(key != _offerKey)
			{
				_offerKey = key;
				_offerTries = 0;
				Input.Pause(600, 1000); // 等選項動畫
			}
			_offerTries++;
			if(_offerTries > 6)
			{
				Status = "選擇畫面卡住了，請截圖回報";
				MechanicMonitor.ReportStuck($"選擇畫面（{string.Join("／", s.Offered.Select(o => o.Name))}{(s.OfferedDarkGift ? "，黑暗之賜" : "")}）",
					s.Offered.Select(o => o.Id), $"選項 {s.Offered.Count} 個，試點 6 次都沒反應；點擊位置：{string.Join(" ", s.Offered.Select(o => $"({o.Screen.X:F0},{o.Screen.Y:F0})"))}");
				Thread.Sleep(2000);
				return;
			}
			var ordered = s.Offered.OrderByDescending(o => o.Cost).ThenByDescending(o => o.Atk + o.Hp).ToList();
			var pick = ordered[(_offerTries - 1) % ordered.Count];
			Status = $"思考中…（選擇：{pick.Name}）";
			Input.Pause(500, 2001);
			Status = $"選擇：{pick.Name}";
			Log.Info($"Choice: {pick.Name} ({_offerTries})");
			Input.Click(pick.Screen);
			Input.Pause(1200, 1800);
		}

		// ───────── 執行動作 ─────────
		private void Execute(Snapshot s, BotAction a)
		{
			if(a.Type != ActionType.EndTurn)
			{
				// 像真人一樣先「想一下」：每個動作前隨機等 0.2～0.6 秒
				Status = "思考中…（" + a.Describe + "）";
				Input.Pause(200, 601);
			}
			Status = a.Describe;

			switch(a.Type)
			{
				case ActionType.EndTurn:
					// 沒有可做的動作（例如沒水晶）：馬上按結束回合
					Input.Pause(150, 400);
					Input.Click(s.EndTurn);
					_endTurnAttempts++;
					WaitUntil(x => !x.MyTurn, 5000);
					if(_endTurnAttempts > 3)
						Status = "結束回合按鈕好像沒按到，請回報位置";
					break;

				case ActionType.Play:
					PlayCard(s, a);
					break;

				case ActionType.HeroPower:
					HeroPower(s, a);
					break;

				case ActionType.Attack:
					DoAttack(a);
					break;
			}
			Input.Hover(s.Neutral);
			Input.Pause(300, 600); // 之後由 Settled() 等畫面穩定再做下一步
		}

		private void PlayCard(Snapshot s, BotAction a)
		{
			var c = a.Source;
			// 先把滑鼠指到那張牌，讀遊戲記憶體確認放大的是它，才按下拖曳（避免拖到旁邊的牌）
			Input_Neutral = s.Neutral;
			if(!PickFromHand(s, c))
			{
				if(RetryLater(c))
					return;
				_skip.Add(c.Id);
				Log.Info($"Hand pick failed: {c.Name}");
				MechanicMonitor.PlayFailure(c.Id, c.Name, "手牌中指不到這張牌（已逐格確認，遊戲記憶體中找不到相同卡牌）");
				return;
			}
			var dest = c.Kind == Kind.Spell && a.Target != null ? a.Target.Screen   // 指定目標法術：直接拖到目標上
				: c.Kind == Kind.Minion ? s.BoardDrop : s.BoardCenter;
			Input.PressAndDrag(dest);

			// 等結算：牌離開手牌／出現在場上／法力已扣，任一成立就算成功（HDT log 有延遲）
			var before = s;
			bool Done(Snapshot x) => Verify.PlayResolved(before, x, c);
			var battlecry = c.Kind == Kind.Minion && (c.Target != TargetKind.None
				|| (c.Text ?? "").IndexOf("battlecry", StringComparison.OrdinalIgnoreCase) >= 0);
			if(WaitUntil(Done, battlecry ? 2500 : 5000))
			{
				Input.Pause(700, 1100);
				ObserveEffect(before, c, c.Kind == Kind.Spell ? a.Target : null);
				return;
			}

			// 有戰吼的手下還沒結算：多半是在等選目標
			if(battlecry)
			{
				var now = Read();
				var kind = c.Target != TargetKind.None && c.Target != TargetKind.Unknown ? c.Target : TargetKind.EnemyAny;
				var t = Planner.PickAnyTarget(now, c, kind); // 一定要選一個，取消會把整張牌退回
				var target = t != null ? FindById(now, t.Id) : null;
				if(target != null)
				{
					Input.Click(target.Screen);
					if(WaitUntil(Done, 4000))
					{
						Input.Pause(500, 800);
						ObserveEffect(now, c, target);
						return;
					}
				}
				else if(WaitUntil(Done, 2500))
					return;
			}

			// 最後再確認一次，避免誤判
			Thread.Sleep(1500);
			if(Done(Read()))
				return;

			Input.RightClick(); // 取消
			Input.Pause(300, 500);
			if(a.Trial)
			{
				TrialFailed(c, a.Target, s);
				return;
			}
			if(RetryLater(c))
				return;
			_skip.Add(c.Id);
			Log.Info($"Play failed {MaxPlayTries} times, skip {c.Name} this turn");
			MechanicMonitor.PlayFailure(c.Id, c.Name,
				$"出牌失敗（重試 {MaxPlayTries} 次）：拖曳{(c.Kind == Kind.Minion ? "到板面" : a.Target != null ? $"到目標「{a.Target.Name}」" : "到場中")}後仍在手上" +
				$"（AI 判斷：類型 {c.Kind}、目標 {c.Target}、費用 {c.Cost}、當時法力 {s.Mana}）");
		}

		private void HeroPower(Snapshot s, BotAction a)
		{
			var hp = a.Source;
			if(a.Target != null)
				Input.Drag(hp.Screen, a.Target.Screen);
			else
				Input.Click(hp.Screen);
			var before = s;
			var ok = WaitUntil(x => Verify.HeroPowerResolved(before, x), 4000);
			if(ok)
				ObserveEffect(before, hp, a.Target);
			else if(a.Trial)
			{
				Input.RightClick();
				TrialFailed(hp, a.Target, s);
			}
			else
			{
				Input.RightClick();
				_skip.Add(hp.Id);
				Log.Info("Hero power failed, skip this turn");
				MechanicMonitor.PlayFailure(hp.Id, "英雄技能 " + hp.Name, $"英雄技能使用失敗（AI 判斷目標 {hp.Target}，選的目標 {a.Target?.Name ?? "無"}）");
			}
		}

		private void DoAttack(BotAction a)
		{
			var src = a.Source;
			var before = src.AttacksThisTurn;
			Input.Drag(src.Screen, a.Target.Screen);
			var ok = WaitUntil(x =>
			{
				var me = FindById(x, src.Id);
				return me == null || me.AttacksThisTurn > before;
			}, 4500);
			if(!ok)
			{
				Input.RightClick();
				_skip.Add(src.Id);
				Log.Info($"Attack failed, skip {src.Name} this turn");
				MechanicMonitor.PlayFailure(src.Id, "攻擊 " + src.Name, $"攻擊「{a.Target.Name}」沒成功（攻擊者 {src.Atk}/{src.Hp}，可能有不能攻擊的新機制或位置偏差）");
			}
			else
				Input.Pause(600, 1000); // 攻擊動畫
		}

		/// <summary>
		/// 指到手牌並確認：滑鼠移到估計位置 → 讀遊戲記憶體「目前放大的牌」→ 對了就回傳 true（游標停在牌上）。
		/// 不對就依「指到的牌在 HDT 手牌中的位置」推算偏移，往正確方向逐格找，最多 10 次。
		/// 讀不到記憶體（舊版 HearthMirror）時退回原本做法。
		/// </summary>
		private bool PickFromHand(Snapshot s, Ent c)
		{
			var idx = s.Hand.FindIndex(h => h.Id == c.Id);
			var n = Math.Max(1, Math.Min(10, s.Hand.Count));
			if(idx < 0 || !s.HandLayouts.ContainsKey(n))
				return false;
			Point At(int size, int i) => s.HandLayouts[Math.Max(1, Math.Min(10, size))][Math.Max(0, Math.Min(size - 1, i))];

			Input.MoveSlow(At(n, idx));
			Thread.Sleep(260);
			var big = LiveState.ReadBigCard();
			if(big == null)
				return true; // 讀不到即時資料：照舊直接拖

			var tried = new HashSet<int>();
			for(var attempt = 0; attempt < 10; attempt++)
			{
				if(big.IsHand && big.CardId == c.CardId)
					return true;
				var liveN = big.IsHand && big.ZoneSize > 0 ? Math.Min(10, big.ZoneSize) : n;
				var cur = big.IsHand ? big.ZonePosition - 1 : -1;
				if(cur >= 0) tried.Add(cur);

				// 推算：指到的牌在 HDT 手牌的位置 vs 實際位置 → 偏移量
				var predicted = Math.Min(idx, liveN - 1);
				if(cur >= 0)
				{
					var hdtIdx = s.Hand.Select((h, i) => (h, i)).Where(x => x.h.CardId == big.CardId)
						.OrderBy(x => Math.Abs(x.i - cur)).Select(x => x.i).DefaultIfEmpty(-1).First();
					if(hdtIdx >= 0)
						predicted = Math.Max(0, Math.Min(liveN - 1, idx + (cur - hdtIdx)));
				}
				var next = Enumerable.Range(0, liveN).Where(k => !tried.Contains(k))
					.OrderBy(k => Math.Abs(k - predicted)).DefaultIfEmpty(-1).First();
				if(next < 0)
					return false;
				tried.Add(next);
				Input.MoveSlow(At(liveN, next));
				Thread.Sleep(260);
				big = LiveState.ReadBigCard();
				if(big == null)
					return false;
			}
			return false;
		}

		private string _lastSig = "";
		private readonly Dictionary<int, int> _playFails = new Dictionary<int, int>();
		private const int MaxPlayTries = 3;
		private bool _idleSeen;

		/// <summary>出牌被退回：還沒試滿 3 次就等一下重新出（回傳 true）；試滿才略過這張牌。</summary>
		private bool RetryLater(Ent c)
		{
			_playFails.TryGetValue(c.Id, out var n);
			_playFails[c.Id] = ++n;
			if(n >= MaxPlayTries)
				return false;
			Status = $"{c.Name} 被退回，稍等重新出牌（第 {n + 1} 次）";
			Log.Info($"Play bounced: {c.Name}, retry {n + 1}/{MaxPlayTries}");
			Input.Hover(Input_Neutral);
			Input.Pause(900, 1400); // 等手牌歸位、畫面穩定
			return true;
		}
		private System.Windows.Point Input_Neutral;

		/// <summary>確認已經沒事可做：沒水晶、沒 0 費可出的牌、英雄技能不能用、沒手下能攻擊。</summary>
		private bool NothingToDo(Snapshot s)
		{
			if(s.Offered.Count > 0)
				return false;
			// 幸運幣／加水晶法術本身不算「有事可做」；它們只有在能讓別張牌出得去時才算
			bool IsManaCard(Ent c) => c.IsCoin || c.ManaGain > 0;
			var extra = s.Hand.Where(c => IsManaCard(c) && c.Cost <= s.Mana && !_skip.Contains(c.Id))
				.Sum(c => c.IsCoin ? 1 : c.ManaRefresh ? Math.Min(c.ManaGain, s.ManaSpent) : c.ManaGain);
			var playable = s.Hand.Any(c => !IsManaCard(c) && c.Cost <= s.Mana + extra && !_skip.Contains(c.Id)
				&& (c.Kind == Kind.Minion ? s.MyBoard.Count(m => m.Kind == Kind.Minion) < 7 : c.Kind != Kind.Location && c.Kind != Kind.Other)
				&& (c.Kind != Kind.Spell || c.Target == TargetKind.None
				    || (c.Target == TargetKind.EnemyMinion ? s.OppBoard.Any(m => m.Kind == Kind.Minion && !m.Stealth)
					    : c.Target == TargetKind.FriendlyMinion ? s.MyBoard.Any(m => m.Kind == Kind.Minion)
					    : c.Target == TargetKind.AnyMinion ? s.MyBoard.Concat(s.OppBoard).Any(m => m.Kind == Kind.Minion) && c.Special != "twoTargets"
					    : c.Target != TargetKind.Unknown)));
			if(playable)
				return false;
			var hp = s.MyHeroPower;
			if(hp != null && !hp.Exhausted && hp.Cost <= s.Mana && !_skip.Contains(hp.Id))
				return false;
			bool CanAttack(Ent a) => a != null && !_skip.Contains(a.Id) && a.Atk > 0 && !a.Frozen && !a.CantAttack && !a.Dormant
			                         && a.AttacksThisTurn < a.MaxAttacks
			                         && (a.Kind == Kind.Minion && a.Rush && !a.Charge && a.TurnsInPlay == 0
				                         ? s.OppBoard.Any(m => m.Kind == Kind.Minion && !m.Stealth)   // 突襲當回合只能打手下
				                         : !a.Exhausted || a.Charge);
			if(s.MyBoard.Any(m => m.Kind == Kind.Minion && CanAttack(m)) || CanAttack(s.MyHero))
				return false;
			return true;
		}
		private int _offsetMine, _offsetTheirs, _offsetMisses;
		private DateTime _unsettledSince = DateTime.MinValue;

		/// <summary>
		/// 畫面穩定才行動：HDT 讀到的狀態連續兩次相同，且場上數量跟遊戲記憶體一致（召喚動畫結束）。
		/// 最多等 3 秒，超過就照目前狀態行動，避免卡住。
		/// </summary>
		private bool Settled(Snapshot s)
		{
			var sig = string.Join(",", s.Hand.Select(h => h.Id)) + "|" + string.Join(",", s.MyBoard.Select(m => m.Id + ":" + m.Hp))
			          + "|" + string.Join(",", s.OppBoard.Select(m => m.Id + ":" + m.Hp)) + "|" + s.Mana + "|" + s.Offered.Count;
			var mine = LiveState.ReadBoard(true);
			var theirs = LiveState.ReadBoard(false);
			var dMine = mine == null ? 0 : mine.Count - s.MyBoard.Count - _offsetMine;
			var dTheirs = theirs == null ? 0 : theirs.Count - s.OppBoard.Count - _offsetTheirs;
			var countsOk = dMine == 0 && dTheirs == 0;
			var stable = sig == _lastSig;
			_lastSig = sig;
			if(stable && countsOk)
			{
				_unsettledSince = DateTime.MinValue;
				_offsetMisses = 0;
				return true;
			}
			if(_unsettledSince == DateTime.MinValue)
				_unsettledSince = DateTime.Now;
			if((DateTime.Now - _unsettledSince).TotalSeconds <= 3)
				return false;
			// 等了 3 秒狀態都沒變但數量還是對不上：可能是記憶體清單多算了別的東西，連續 2 次就記成固定差值
			if(stable && !countsOk && ++_offsetMisses >= 2)
			{
				_offsetMine += dMine;
				_offsetTheirs += dTheirs;
				_offsetMisses = 0;
				Log.Info($"LiveBoard offset learned: mine {_offsetMine}, theirs {_offsetTheirs}");
			}
			return true;
		}

		private static Ent FindById(Snapshot s, int id) =>
			s.MyBoard.Concat(s.OppBoard).Concat(new[] { s.MyHero, s.OppHero }).FirstOrDefault(e => e != null && e.Id == id);

		private bool WaitUntil(Func<Snapshot, bool> cond, int timeoutMs)
		{
			var end = DateTime.Now.AddMilliseconds(timeoutMs);
			while(DateTime.Now < end && _running)
			{
				Thread.Sleep(200);
				var s = Read();
				if(!s.InGame || s.GameOver || cond(s))
					return true;
			}
			return false;
		}
	}
}
