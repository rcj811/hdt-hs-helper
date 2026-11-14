using System;
using System.Windows.Controls;
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
		private DateTime _lastRefresh = DateTime.MinValue;
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

		public string Name => "爐石輔助 HsHelper";
		public string Description => "即時顯示遊戲狀態（回合、手牌、牌庫、場面、戰場酒館等級）。";
		public string ButtonText => "顯示／隱藏面板";
		public string Author => "rcj811";
		public Version Version => new Version(0, 1, 1);
		public MenuItem MenuItem => _menuItem;

		public void OnLoad()
		{
			try
			{
				_panel = new StatePanel();
				ApiCore.OverlayCanvas.Children.Add(_panel);
				// 放在左下方空白處，避開 HDT 左上角的對手記牌清單
				Canvas.SetLeft(_panel, 12);
				PositionPanel();
				ApiCore.OverlayCanvas.SizeChanged += (s, e) => PositionPanel();

				_menuItem = new MenuItem { Header = "爐石輔助：顯示／隱藏面板" };
				_menuItem.Click += (s, e) => TogglePanel();

				Log.Info("HsHelper loaded");
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		public void OnUnload()
		{
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
			if(_panel == null || DateTime.Now - _lastRefresh < RefreshInterval)
				return;
			_lastRefresh = DateTime.Now;
			try
			{
				_panel.Render(GameStateReader.Read());
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

		private void TogglePanel()
		{
			if(_panel != null)
				_panel.UserHidden = !_panel.UserHidden;
		}
	}
}
