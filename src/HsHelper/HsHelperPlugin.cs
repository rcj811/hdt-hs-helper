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
		private StatePanel _panel;
		private MenuItem _menuItem;
		private readonly AutoPlayer _auto = new AutoPlayer();
		private bool _f8WasDown;
		private Toast _toast;
		private DateTime _lastRefresh = DateTime.MinValue;
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

		public string Name => "爐石輔助 HsHelper";
		public string Description => "即時顯示遊戲狀態；按 F8 開始／停止自動出牌（構築／練習模式）。";
		public string ButtonText => "顯示／隱藏面板";
		public string Author => "rcj811";
		public Version Version => new Version(0, 4, 0);
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

				_menuItem = new MenuItem { Header = "爐石輔助" };
				var panelItem = new MenuItem { Header = "顯示／隱藏面板" };
				panelItem.Click += (s, e) => TogglePanel();
				var autoItem = new MenuItem { Header = "自動出牌 開始／停止（F8）" };
				autoItem.Click += (s, e) => ToggleAuto();
				var nextItem = new MenuItem { Header = "對戰結束後自動下一場", IsCheckable = true, IsChecked = _auto.AutoNext };
				nextItem.Click += (s, e) => _auto.AutoNext = nextItem.IsChecked;
				_menuItem.Items.Add(panelItem);
				_menuItem.Items.Add(autoItem);
				_menuItem.Items.Add(nextItem);
				_menuItem.Items.Add(new Separator());
				var exportItem = new MenuItem { Header = "匯出學習資料…" };
				exportItem.Click += (s, e) => ExportLearning();
				var importItem = new MenuItem { Header = "匯入學習資料…（自動比對去重）" };
				importItem.Click += (s, e) => ImportLearning();
				_menuItem.Items.Add(exportItem);
				_menuItem.Items.Add(importItem);

				Log.Info("HsHelper loaded");
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
			try
			{
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

		private void ImportLearning()
		{
			try
			{
				var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "學習資料 (*.json)|*.json", Multiselect = true };
				if(dlg.ShowDialog() != true)
					return;
				int added = 0, skipped = 0;
				foreach(var f in dlg.FileNames)
				{
					var (a, sk) = _auto.Import(f);
					added += a;
					skipped += sk;
				}
				_toast?.Show($"匯入完成：新增 {added} 筆，略過重複 {skipped} 筆", Color.FromRgb(0x6B, 0xC5, 0xF8));
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
