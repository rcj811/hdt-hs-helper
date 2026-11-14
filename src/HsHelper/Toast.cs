using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper
{
	/// <summary>畫面中央的大字提示（例如按 F8 後顯示「已啟動自動打牌」）。</summary>
	public class Toast
	{
		private readonly Border _box;
		private readonly TextBlock _text;
		private readonly DispatcherTimer _timer;

		public Toast()
		{
			_text = new TextBlock
			{
				FontSize = 30,
				FontWeight = FontWeights.Bold,
				Foreground = Brushes.White,
				FontFamily = new FontFamily("Microsoft JhengHei UI, Segoe UI"),
				TextAlignment = TextAlignment.Center
			};
			_box = new Border
			{
				Child = _text,
				CornerRadius = new CornerRadius(10),
				Padding = new Thickness(28, 14, 28, 14),
				BorderThickness = new Thickness(2),
				IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			ApiCore.OverlayCanvas.Children.Add(_box);
			_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
			_timer.Tick += (s, e) => { _timer.Stop(); _box.Visibility = Visibility.Collapsed; };
		}

		public void Show(string message, Color color, bool sound = true)
		{
			_text.Text = message;
			_box.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x15, 0x15, 0x1C));
			_box.BorderBrush = new SolidColorBrush(color);
			_text.Foreground = new SolidColorBrush(color);
			_box.Visibility = Visibility.Visible;
			_box.UpdateLayout();
			var canvas = ApiCore.OverlayCanvas;
			Canvas.SetLeft(_box, Math.Max(0, (canvas.ActualWidth - _box.ActualWidth) / 2));
			Canvas.SetTop(_box, Math.Max(0, canvas.ActualHeight * 0.30 - _box.ActualHeight / 2));
			_timer.Stop();
			_timer.Start();
			if(sound)
				SystemSounds.Asterisk.Play();
		}

		public void Remove() => ApiCore.OverlayCanvas.Children.Remove(_box);
	}
}
