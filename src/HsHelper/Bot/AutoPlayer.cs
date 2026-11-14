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
		/// <summary>對戰結束後自動點掉結算／獎勵畫面，並按「開始」進下一場。</summary>
		public bool AutoNext { get; set; } = true;
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
				if(s.GameOver) _sawGameOver = true;
				HandleMenus(s.GameOver);
				return;
			}
			_playClicks = 0;
			_sawGameOver = false;

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
				_skipTurn = s.Turn;
				Input.Pause(1200, 2000); // 回合開始動畫
				return;
			}

			if(s.Offered.Count > 0)
			{
				ChooseOffered(s);
				return;
			}

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
			Status = action.Describe;
			Log.Info("Action: " + action.Describe);
			Execute(s, action);
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

		// ───────── 發現／抉擇 ─────────
		private void ChooseOffered(Snapshot s)
		{
			var pick = s.Offered.OrderByDescending(o => o.Cost).ThenByDescending(o => o.Atk + o.Hp).First();
			Status = $"選擇：{pick.Name}";
			Input.Click(pick.Screen);
			Input.Pause(1200, 1800);
		}

		// ───────── 執行動作 ─────────
		private void Execute(Snapshot s, BotAction a)
		{
			// 像真人一樣先「想一下」：每個動作前隨機等 0.5～2 秒
			Status = "思考中…（" + a.Describe + "）";
			Input.Pause(500, 2001);
			Status = a.Describe;

			switch(a.Type)
			{
				case ActionType.EndTurn:
					Input.Pause(900, 1600);
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
			Input.Pause(1200, 2000); // 放慢：每個動作之間停 1.2～2 秒
		}

		private void PlayCard(Snapshot s, BotAction a)
		{
			var c = a.Source;
			if(c.Kind == Kind.Spell && a.Target != null)
				Input.Drag(c.Screen, a.Target.Screen); // 指定目標法術：直接拖到目標上
			else if(c.Kind == Kind.Minion)
				Input.Drag(c.Screen, s.BoardDrop);
			else
				Input.Drag(c.Screen, s.BoardCenter);

			var left = WaitUntil(x => x.Hand.All(h => h.Id != c.Id), 1600);
			if(left)
			{
				Input.Pause(700, 1100);
				return;
			}

			// 還在手上：可能是需要目標的戰吼，或拖曳失敗
			if(c.Kind == Kind.Minion)
			{
				var now = Read();
				var kind = c.Target != TargetKind.None ? c.Target : TargetKind.EnemyAny;
				var t = Planner.PickTarget(now, c, kind);
				if(t != null)
				{
					var target = FindById(now, t.Id);
					if(target != null)
					{
						Input.Click(target.Screen);
						if(WaitUntil(x => x.Hand.All(h => h.Id != c.Id), 1800))
							return;
					}
				}
			}
			Input.RightClick(); // 取消
			Input.Pause(300, 500);
			_skip.Add(c.Id);
			Log.Info($"Play failed, skip {c.Name} this turn");
		}

		private void HeroPower(Snapshot s, BotAction a)
		{
			var hp = a.Source;
			if(a.Target != null)
				Input.Drag(hp.Screen, a.Target.Screen);
			else
				Input.Click(hp.Screen);
			var ok = WaitUntil(x => x.MyHeroPower == null || x.MyHeroPower.Exhausted, 1800);
			if(!ok)
			{
				Input.RightClick();
				_skip.Add(hp.Id);
				Log.Info("Hero power failed, skip this turn");
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
			}, 2500);
			if(!ok)
			{
				Input.RightClick();
				_skip.Add(src.Id);
				Log.Info($"Attack failed, skip {src.Name} this turn");
			}
			else
				Input.Pause(600, 1000); // 攻擊動畫
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
