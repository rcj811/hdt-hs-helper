using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HsHelper
{
	/// <summary>覆蓋在遊戲畫面上的半透明狀態面板（純程式碼建立，不用 XAML）。</summary>
	public class StatePanel : Border
	{
		private readonly StackPanel _stack = new StackPanel();
		private bool _userHidden;

		private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x6A));
		private static readonly Brush TextBrush = Brushes.White;
		private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

		public StatePanel()
		{
			Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x22));
			BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x6D, 0x3B));
			BorderThickness = new Thickness(1);
			CornerRadius = new CornerRadius(6);
			Padding = new Thickness(10, 8, 10, 8);
			MinWidth = 210;
			IsHitTestVisible = false;
			Child = _stack;
			Visibility = Visibility.Collapsed;
		}

		public bool UserHidden
		{
			get => _userHidden;
			set { _userHidden = value; if(value) Visibility = Visibility.Collapsed; }
		}

		private static readonly Brush AutoOnBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0xE0, 0x7A));

		public void Render(GameSnapshot s, bool autoOn, string autoStatus)
		{
			if(_userHidden || !s.InGame)
			{
				Visibility = Visibility.Collapsed;
				return;
			}
			Visibility = Visibility.Visible;
			_stack.Children.Clear();

			Add(s.IsBattlegrounds ? "爐石輔助・酒館戰棋" : "爐石輔助・對戰", TitleBrush, 14, true);
			Add($"回合 {s.Turn}", DimBrush);
			if(!s.IsBattlegrounds)
				Add(autoOn ? $"▶ 自動：{autoStatus}" : "自動出牌：關（F8 開始）", autoOn ? AutoOnBrush : DimBrush, 12, autoOn, 2);

			if(s.IsBattlegrounds)
			{
				Add($"我：生命 {s.PlayerHealth}　酒館 {s.TavernTier} 級", TextBrush);
				AddBoard(s.PlayerBoard, true);
				if(s.IsCombatPhase)
				{
					Add($"對手：{s.OpponentName ?? "?"}　生命 {s.OpponentHealth}　酒館 {s.OpponentTavernTier} 級", TextBrush, 12, false, 6);
					AddBoard(s.OpponentBoard, true);
				}
			}
			else
			{
				Add($"我（{s.PlayerClass ?? "?"}）：生命 {s.PlayerHealth}", TextBrush, 12, false, 4);
				Add($"手牌 {s.PlayerHand}　牌庫 {s.PlayerDeck}", DimBrush);
				AddBoard(s.PlayerBoard, false);
				Add($"對手（{s.OpponentClass ?? "?"}）：生命 {s.OpponentHealth}", TextBrush, 12, false, 6);
				Add($"手牌 {s.OpponentHand}　牌庫 {s.OpponentDeck}", DimBrush);
				AddBoard(s.OpponentBoard, false);
			}
		}

		private void AddBoard(List<MinionInfo> board, bool showTier)
		{
			if(board.Count == 0)
			{
				Add("　（場上無手下）", DimBrush, 11);
				return;
			}
			var total = board.Sum(m => m.Attack);
			foreach(var m in board)
			{
				var tier = showTier && m.Tier > 0 ? $"[{m.Tier}] " : "";
				Add($"　{tier}{m.Name}  {m.Attack}/{m.Health}", TextBrush, 11);
			}
			Add($"　場面總攻擊 {total}", DimBrush, 11);
		}

		private void Add(string text, Brush brush, double size = 12, bool bold = false, double topMargin = 0)
		{
			_stack.Children.Add(new TextBlock
			{
				Text = text,
				Foreground = brush,
				FontSize = size,
				FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
				Margin = new Thickness(0, topMargin, 0, 1),
				FontFamily = new FontFamily("Microsoft JhengHei UI, Segoe UI")
			});
		}
	}
}
