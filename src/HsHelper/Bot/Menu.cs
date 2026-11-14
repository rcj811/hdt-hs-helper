using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper.Bot
{
	/// <summary>選單畫面的狀態：目前場景、是否正在排隊配對，以及爐石視窗在螢幕上的位置。</summary>
	public class MenuState
	{
		public Mode Mode;
		public bool InQueue;
		public bool HasWindow;
		public double Left, Top, Width, Height; // 爐石視窗內容區（實體像素）

		public Point At(double dxH, double yH) => new Point(Left + Width / 2 + Height * dxH, Top + Height * yH);
		public Point Center => At(0, 0.5);
		public Point PlayButton => At(0.411, 0.821);   // 「開始」按鈕（依你的截圖量測）
		public Point ContinueText => At(0, 0.88);      // 「點擊以繼續」
	}

	public static class Menu
	{
		[DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
		[DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
		[StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
		[StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

		private static PropertyInfo _queueProp;

		/// <summary>在 UI 執行緒呼叫。</summary>
		public static MenuState Read()
		{
			var m = new MenuState();
			var game = ApiCore.Game;
			if(game != null)
			{
				m.Mode = game.CurrentMode;
				m.InQueue = ReadInQueue(game);
			}
			ReadWindow(m);
			return m;
		}

		// HDT 的 GameV2.QueueEvents 是 internal，用反射讀 IsInQueue（讀不到就當作沒在排隊）
		private static bool ReadInQueue(object game)
		{
			try
			{
				_queueProp ??= game.GetType().GetProperty("QueueEvents", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				var qe = _queueProp?.GetValue(game);
				var p = qe?.GetType().GetProperty("IsInQueue");
				return p != null && (bool)p.GetValue(qe);
			}
			catch(Exception)
			{
				return false;
			}
		}

		private static void ReadWindow(MenuState m)
		{
			try
			{
				foreach(var p in Process.GetProcessesByName("Hearthstone"))
				{
					using(p)
					{
						var h = p.MainWindowHandle;
						if(h == IntPtr.Zero || !GetClientRect(h, out var r))
							continue;
						var pt = new POINT();
						if(!ClientToScreen(h, ref pt))
							continue;
						m.Left = pt.X; m.Top = pt.Y; m.Width = r.R - r.L; m.Height = r.B - r.T;
						m.HasWindow = m.Width > 100 && m.Height > 100;
						return;
					}
				}
			}
			catch(Exception)
			{
				// 忽略
			}
		}
	}
}
