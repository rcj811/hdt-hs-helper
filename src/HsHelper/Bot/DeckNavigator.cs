using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Hearthstone_Deck_Tracker.Utility.Logging;

namespace HsHelper.Bot
{
	/// <summary>
	/// 選套牌畫面翻頁：掃描每一頁有哪些套牌（讀遊戲記憶體），從所有頁中挑預估勝率最高的，翻到那頁點選並確認。
	/// 頁面配置會快取；目前這頁跟快取對得上就不用重掃。每次翻頁都讀記憶體確認真的翻過去了，不會亂點。
	/// </summary>
	public class DeckNavigator
	{
		private List<List<long>> _pages = new List<List<long>>();   // 快取：每頁的套牌 Id（依頁序）
		private readonly Dictionary<long, DeckInfo> _decks = new Dictionary<long, DeckInfo>();

		private static Point? OnUi(Func<Point?> f)
		{
			try { return Application.Current.Dispatcher.Invoke(f); }
			catch(Exception) { return null; }
		}

		private static string Key(DeckPicker.PageState p) => string.Join(",", p.Decks.Select(d => d.Id));

		/// <summary>點一次翻頁箭頭，回傳翻過去後的頁面；沒翻動（已到盡頭）回傳 null。</summary>
		private static DeckPicker.PageState Flip(bool right, DeckPicker.PageState current)
		{
			var pt = OnUi(() => DeckPicker.ArrowPoint(right));
			if(pt == null)
				return null;
			Input.Click(pt.Value);
			for(var i = 0; i < 8; i++)
			{
				Thread.Sleep(250);
				var p = DeckPicker.Read();
				if(p.Ok && Key(p) != Key(current))
				{
					Thread.Sleep(400); // 翻頁動畫
					return DeckPicker.Read();
				}
			}
			return null;
		}

		/// <summary>掃描所有頁（從目前這頁往右，再回來往左），回傳目前所在的頁序。</summary>
		private int Scan(DeckPicker.PageState start)
		{
			var right = new List<List<long>>();
			var left = new List<List<long>>();
			Remember(start);
			var cur = start;
			for(var i = 0; i < 8; i++)
			{
				var next = Flip(true, cur);
				if(next == null) break;
				Remember(next);
				right.Add(next.Decks.Select(d => d.Id).ToList());
				cur = next;
			}
			// 翻回起點
			for(var i = 0; i < right.Count; i++)
			{
				var prev = Flip(false, cur);
				if(prev == null) break;
				cur = prev;
			}
			for(var i = 0; i < 8; i++)
			{
				var prev = Flip(false, cur);
				if(prev == null) break;
				Remember(prev);
				left.Add(prev.Decks.Select(d => d.Id).ToList());
				cur = prev;
			}
			left.Reverse();
			_pages = left.Concat(new[] { start.Decks.Select(d => d.Id).ToList() }).Concat(right).ToList();
			Log.Info($"DeckNavigator: {_pages.Count} pages, {_decks.Count} decks");
			// 目前停在最左頁
			return 0;
		}

		private void Remember(DeckPicker.PageState p)
		{
			foreach(var d in p.Decks)
				_decks[d.Id] = d;
		}

		private int PageOf(DeckPicker.PageState p)
		{
			var key = p.Decks.Select(d => d.Id).ToList();
			return _pages.FindIndex(x => x.SequenceEqual(key));
		}

		/// <summary>
		/// 確保選中「所有頁中預估勝率最高的套牌」。回傳說明文字；選好（或決定用目前的）時 done = true。
		/// </summary>
		public string EnsureBest(LearningDb db, object dbLock, out bool done, out DeckInfo selected)
		{
			done = true;
			selected = null;
			var page = DeckPicker.Read();
			if(!page.Ok)
				return "讀不到選套牌畫面，使用目前的套牌";

			// 目前這頁不在快取裡 → 重新掃描所有頁
			var curIdx = PageOf(page);
			if(curIdx < 0)
			{
				curIdx = Scan(page);
				page = DeckPicker.Read();
				var at = PageOf(page);
				if(at >= 0) curIdx = at;
			}
			else
				Remember(page);

			DeckInfo best;
			string reason;
			lock(dbLock)
				best = DeckChooser.Choose(_decks.Values.ToList(), db, out reason);
			if(best == null)
				return "沒有可用的套牌";

			// 翻到目標頁
			var target = _pages.FindIndex(x => x.Contains(best.Id));
			if(target < 0)
				target = curIdx;
			for(var guard = 0; curIdx != target && guard < 10; guard++)
			{
				var moved = Flip(target > curIdx, page);
				if(moved == null)
				{
					_pages.Clear(); // 頁面配置變了，下次重掃
					return "翻頁失敗，使用目前的套牌";
				}
				page = moved;
				var at = PageOf(page);
				curIdx = at >= 0 ? at : curIdx + (target > curIdx ? 1 : -1);
			}

			var onPage = page.Decks.FirstOrDefault(d => d.Id == best.Id);
			if(onPage == null)
			{
				_pages.Clear();
				return "找不到目標套牌，使用目前的套牌";
			}
			if(page.Selected == best.Id)
			{
				selected = onPage;
				return reason;
			}

			// 點選並確認
			for(var tries = 0; tries < 3; tries++)
			{
				var pt = OnUi(() => DeckPicker.SlotPoint(onPage.Slot));
				if(pt == null) break;
				Input.Click(pt.Value);
				Thread.Sleep(1200);
				var check = DeckPicker.Read();
				if(check.Ok && check.Selected == best.Id)
				{
					selected = onPage;
					return reason;
				}
			}
			return "選套牌沒成功，使用目前的套牌";
		}
	}
}
