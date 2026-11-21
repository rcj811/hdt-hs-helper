using System;
using System.Windows.Controls;
using System.Windows.Media;
using HsHelper.Bot;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Logging;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper
{
	/// <summary>
	/// 爐石輔助 v0.1：在 HDT 的遊戲覆蓋層上顯示即時遊戲狀態面板。
	/// 之後的戰場建議、自動操作都會建立在 GameStateReader 讀到的資料上。
	/// </summary>
	public class HsHelperPlugin : IPlugin
	{
		public static string CardsLine = "";
		private StatePanel _panel;
		private MenuItem _menuItem;
		private readonly AutoPlayer _auto = new AutoPlayer();
		private bool _f8WasDown;
		private Toast _toast;
		private DateTime _lastRefresh = DateTime.MinValue;
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

		/// <summary>版本號只在 HsHelper.csproj 的 &lt;Version&gt; 設定一次，這裡直接讀編譯出來的版本，不會不一致。</summary>
		public static readonly Version PluginVersion = typeof(HsHelperPlugin).Assembly.GetName().Version;
		public static string VersionText => $"v{PluginVersion.Major}.{PluginVersion.Minor}.{PluginVersion.Build}";

		public string Name => $"爐石輔助 HsHelper {VersionText}";
		public string Description => "即時顯示遊戲狀態；按 F8 開始／停止自動出牌（構築／練習模式）。";
		public string ButtonText => "顯示／隱藏面板";
		public string Author => "rcj811";
		public Version Version => PluginVersion;
		public MenuItem MenuItem => _menuItem;

		public void OnLoad()
		{
			try
			{
				_panel = new StatePanel();
				ApiCore.OverlayCanvas.Children.Add(_panel);
				// 放在左下方空白處，避開 HDT 左上角的對手記牌清單
				_toast = new Toast();
				Canvas.SetLeft(_panel, 12);
				PositionPanel();
				ApiCore.OverlayCanvas.SizeChanged += (s, e) => PositionPanel();

				_menuItem = new MenuItem { Header = $"爐石輔助 {VersionText}" };
				var panelItem = new MenuItem { Header = "顯示／隱藏面板" };
				panelItem.Click += (s, e) => TogglePanel();
				var autoItem = new MenuItem { Header = "自動出牌 開始／停止（F8）" };
				autoItem.Click += (s, e) => ToggleAuto();
				var nextItem = new MenuItem { Header = "對戰結束後自動下一場", IsCheckable = true, IsChecked = _auto.AutoNext };
				nextItem.Click += (s, e) => _auto.AutoNext = nextItem.IsChecked;
				_menuItem.Items.Add(panelItem);
				_menuItem.Items.Add(autoItem);
				_menuItem.Items.Add(nextItem);
				var deckItem = new MenuItem { Header = "自動選勝率最高的套牌", IsCheckable = true, IsChecked = _auto.AutoDeck };
				deckItem.Click += (s, e) => _auto.AutoDeck = deckItem.IsChecked;
				_menuItem.Items.Add(deckItem);
				_menuItem.Items.Add(new Separator());
				var exportItem = new MenuItem { Header = "匯出學習資料…" };
				exportItem.Click += (s, e) => ExportLearning();
				var importItem = new MenuItem { Header = "匯入學習資料…（自動比對去重）" };
				importItem.Click += (s, e) => ImportLearning();
				_menuItem.Items.Add(exportItem);
				_menuItem.Items.Add(importItem);
				_menuItem.Items.Add(new Separator());
				var noticeItem = new MenuItem { Header = "查看新機制通知…" };
				noticeItem.Click += (s, e) =>
				{
					try
					{
						if(!System.IO.File.Exists(MechanicMonitor.NoticePath))
							_toast?.Show("目前沒有新機制通知", Color.FromRgb(0x6B, 0xC5, 0xF8), false);
						else
							System.Diagnostics.Process.Start("notepad.exe", "\"" + MechanicMonitor.NoticePath + "\"");
					}
					catch(Exception ex) { Log.Error(ex); }
				};
				_menuItem.Items.Add(noticeItem);
				var reportItem = new MenuItem { Header = "匯出新機制報告（給 Claude）…" };
				reportItem.Click += (s, e) => ExportReport();
				_menuItem.Items.Add(reportItem);

				// 新機制偵測：通知跳在畫面中央
				MechanicMonitor.Notice += msg => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
					_toast?.Show("🔔 " + msg, Color.FromRgb(0xFF, 0xC8, 0x57), true, 20, 7)));
				System.Threading.Tasks.Task.Delay(8000).ContinueWith(_ =>
				{
					try { MechanicMonitor.CheckOnStartup(); } catch(Exception ex) { Log.Error(ex); }
				});

				// 新版檢查：GitHub 有新版就提示，選單也多一項「下載新版」
				System.Threading.Tasks.Task.Delay(12000).ContinueWith(async _ =>
				{
					var tag = await UpdateCheck.NewerAsync(PluginVersion);
					if(tag == null) return;
					System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
					{
						_toast?.Show($"⬆ 爐石輔助有新版 {tag}（目前 {VersionText}）\n選單「爐石輔助 → 下載新版」", Color.FromRgb(0x7C, 0xE0, 0x7C), true, 20, 8);
						var upd = new MenuItem { Header = $"⬆ 下載新版 {tag}…" };
						upd.Click += (s2, e2) => { try { System.Diagnostics.Process.Start(UpdateCheck.ReleasesPage); } catch(Exception ex) { Log.Error(ex); } };
						_menuItem.Items.Insert(0, upd);
					}));
				});

				Log.Info($"HsHelper {VersionText} loaded");
				// 啟動提示：確認更新到的版本
				System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
					_toast?.Show($"爐石輔助 {VersionText} 已載入", Color.FromRgb(0x6B, 0xC5, 0xF8), false, 22, 4))));
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		public void OnUnload()
		{
			_auto.Stop();
			_toast?.Remove();
			if(_panel != null)
			{
				ApiCore.OverlayCanvas.Children.Remove(_panel);
				_panel = null;
			}
		}

		public void OnButtonPress() => TogglePanel();

		// HDT 大約每 100ms 呼叫一次；面板每 500ms 更新就夠了
		public void OnUpdate()
		{
			// F8：自動出牌開關（偵測按下的瞬間）
			var f8 = Input.KeyDown(Input.VK_F8);
			if(f8 && !_f8WasDown)
				ToggleAuto();
			_f8WasDown = f8;

			if(_panel == null || DateTime.Now - _lastRefresh < RefreshInterval)
				return;
			_lastRefresh = DateTime.Now;
			try { MechanicMonitor.CheckInGame(); } catch(Exception ex) { Log.Error(ex); }
			try
			{
				CardsLine = _auto.CardsText;
				_panel.Render(GameStateReader.Read(), _auto.Running, _auto.Status, _auto.StyleText);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		private void PositionPanel()
		{
			if(_panel == null)
				return;
			var h = ApiCore.OverlayCanvas.ActualHeight;
			Canvas.SetBottom(_panel, h > 0 ? h * 0.17 : 150);
		}

		private void ToggleAuto()
		{
			_auto.Toggle();
			if(_auto.Running)
			{
				// 啟動時把 HDT 主視窗縮小，並把爐石切到最前面，避免擋住遊戲
				try
				{
					foreach(System.Windows.Window w in System.Windows.Application.Current.Windows)
					{
						// HDT 主視窗（類型名稱 MainWindow），不碰覆蓋層等其他視窗
						if(w.GetType().Name == "MainWindow" && w.WindowState != System.Windows.WindowState.Minimized)
							w.WindowState = System.Windows.WindowState.Minimized;
					}
				}
				catch(Exception ex)
				{
					Log.Error(ex);
				}
				Input.FocusHearthstone();
				_toast?.Show("▶ 已啟動自動打牌", Color.FromRgb(0x6B, 0xE0, 0x7A));
			}
			else
				_toast?.Show("■ 已停止自動打牌", Color.FromRgb(0xFF, 0x6B, 0x6B));
		}

		private void ExportLearning()
		{
			try
			{
				var dlg = new Microsoft.Win32.SaveFileDialog
				{
					FileName = $"hshelper-learning-{DateTime.Now:yyyyMMdd}.json",
					Filter = "學習資料 (*.json)|*.json"
				};
				if(dlg.ShowDialog() != true)
					return;
				_auto.Export(dlg.FileName);
				int n;
				lock(_auto.DbLock) n = _auto.Db.Games.Count;
				_toast?.Show($"已匯出 {n} 筆學習資料", Color.FromRgb(0x6B, 0xC5, 0xF8));
			}
			catch(Exception ex)
			{
				Log.Error(ex);
				_toast?.Show("匯出失敗：" + ex.Message, Color.FromRgb(0xFF, 0x6B, 0x6B));
			}
		}

		private void ExportReport()
		{
			try
			{
				var dlg = new Microsoft.Win32.SaveFileDialog
				{
					FileName = $"hshelper-mechanics-report-{DateTime.Now:yyyyMMdd-HHmm}.zip",
					Filter = "新機制報告 (*.zip)|*.zip"
				};
				if(dlg.ShowDialog() != true)
					return;
				MechanicMonitor.ExportZip(dlg.FileName);
				_toast?.Show($"已匯出新機制報告（待處理 {MechanicMonitor.PendingCount} 項），把 zip 傳給 Claude 即可",
					Color.FromRgb(0x6B, 0xC5, 0xF8), false, 20, 5);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
				_toast?.Show("匯出報告失敗：" + ex.Message, Color.FromRgb(0xFF, 0x6B, 0x6B), true, 20, 5);
			}
		}

		private void ImportLearning()
		{
			try
			{
				var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "學習資料 (*.json)|*.json", Multiselect = true };
				if(dlg.ShowDialog() != true)
					return;
				int added = 0, skipped = 0;
				var lessons = 0;
				foreach(var f in dlg.FileNames)
				{
					var (a, sk) = _auto.Import(f);
					added += a;
					skipped += sk;
					lessons += _auto.Db.LastLessonsMerged;
				}
				_toast?.Show($"匯入完成：新增 {added} 筆，略過重複 {skipped} 筆" + (lessons > 0 ? $"，卡牌效果 {lessons} 張" : ""), Color.FromRgb(0x6B, 0xC5, 0xF8));
			}
			catch(Exception ex)
			{
				Log.Error(ex);
				_toast?.Show("匯入失敗：檔案格式不正確", Color.FromRgb(0xFF, 0x6B, 0x6B));
			}
		}

		private void TogglePanel()
		{
			if(_panel != null)
				_panel.UserHidden = !_panel.UserHidden;
		}
	}
}
