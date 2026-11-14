using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HearthDb.Enums;
using HearthMirror;
using Hearthstone_Deck_Tracker.Utility.Logging;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper.Bot
{
	/// <summary>
	/// 選套牌畫面：從遊戲記憶體讀出這一頁的套牌（HearthMirror，與 HDT 的 DeckPickerWatcher 相同來源），
	/// 挑預估勝率最高的那副，點選後再讀一次確認真的選中。
	/// 格子位置沿用 HDT 套牌勝率覆蓋層的排版：左上角 (0.087 寬度比例, 0.217 高)，以 1080 高度為基準每格寬 241、列距 224，每頁 3×3。
	/// </summary>
	public static class DeckPicker
	{
		public class PageState
		{
			public List<DeckInfo> Decks = new List<DeckInfo>();
			public long? Selected;
			public bool Ok;
		}

		/// <summary>讀取選套牌畫面這一頁的套牌（可在背景執行緒呼叫）。</summary>
		public static PageState Read()
		{
			var st = new PageState();
			try
			{
				var page = Reflection.Client.GetDeckPickerDecksOnPage();
				var state = Reflection.Client.GetDeckPickerState();
				var all = Reflection.Client.GetDecks();
				if(page == null || all == null)
					return st;
				st.Selected = state?.SelectedDeck;
				for(var slot = 0; slot < page.Count; slot++)
				{
					var box = page[slot];
					if(box?.DeckId is not long id)
						continue;
					if(box.IsShowingInvalidCardCount || box.InvalidSideboardCardCount > 0 || box.MissingSideboardCardCount > 0)
						continue; // 不完整的套牌
					var deck = all.FirstOrDefault(d => d.Id == id);
					if(deck == null)
						continue;
					var info = DeckChooser.Analyze(id, deck.Name, deck.Cards.Select(c =>
					{
						HearthDb.Cards.All.TryGetValue(c.Id, out var card);
						return (card?.Type == CardType.MINION, card?.Cost ?? 0, card?.GetLocText(Locale.enUS) ?? "", c.Count);
					}));
					info.Slot = slot;
					st.Decks.Add(info);
				}
				st.Ok = st.Decks.Count > 0;
			}
			catch(Exception ex)
			{
				Log.Warn("DeckPicker read failed: " + ex.Message);
			}
			return st;
		}

		/// <summary>第 slot 格套牌的螢幕座標（在 UI 執行緒呼叫）。</summary>
		public static Point? SlotPoint(int slot)
		{
			try
			{
				var ow = ApiCore.OverlayWindow;
				double W = ow.Width, H = ow.Height;
				if(double.IsNaN(W) || W < 100 || H < 100)
					return null;
				var ratio = (4.0 / 3.0) / (W / H);
				var left = W * ratio * 0.087 + W * (1 - ratio) / 2;
				var top = H * 0.217;
				var scale = H / 1080;
				var col = slot % 3;
				var row = slot / 3;
				var x = left + (col * 241 + 119) * scale;
				var y = top + (row * 224 + 95) * scale;
				return ow.PointToScreen(new Point(x, y));
			}
			catch(Exception)
			{
				return null;
			}
		}
	}
}
