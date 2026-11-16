using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HsHelper.Bot
{
	/// <summary>
	/// 直接讀遊戲記憶體的即時狀態（HearthMirror，不經過 log，所以沒有延遲）：
	///  - BigCard：滑鼠指著手牌時，遊戲放大的是哪張（用來確認拖的是對的牌）
	///  - Board：場上即時的手下數量（用來算攻擊位置，避免 HDT 資料少算正在召喚的手下）
	/// 用反射呼叫，HDT／HearthMirror 版本不同時不會讓外掛壞掉（讀不到就回傳 null，退回原本做法）。
	/// </summary>
	public static class LiveState
	{
		public class BigCard
		{
			public bool IsHand;
			public string CardId = "";
			public int ZonePosition; // 1 起算
			public int ZoneSize;
		}

		public class BoardSide
		{
			public int Count;
			public List<int> EntityIds; // 讀得到才有
		}

		private static object Client()
		{
			try { return typeof(HearthMirror.Reflection).GetProperty("Client", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
			catch(Exception) { return null; }
		}

		private static object Get(object obj, string name)
		{
			if(obj == null) return null;
			var t = obj.GetType();
			return t.GetProperty(name)?.GetValue(obj) ?? t.GetField(name)?.GetValue(obj);
		}

		private static MethodInfo _bigCard, _board;

		public static BigCard ReadBigCard()
		{
			try
			{
				var c = Client();
				if(c == null) return null;
				_bigCard ??= c.GetType().GetMethod("GetBigCardState", System.Type.EmptyTypes);
				var st = _bigCard?.Invoke(c, null);
				if(st == null) return new BigCard();
				return new BigCard
				{
					IsHand = Get(st, "IsHand") is bool b && b,
					CardId = Get(st, "CardId") as string ?? "",
					ZonePosition = Get(st, "ZonePosition") is int p ? p : 0,
					ZoneSize = Get(st, "ZoneSize") is int z ? z : 0
				};
			}
			catch(Exception)
			{
				return null;
			}
		}

		/// <summary>讀場上即時的手下（friendly=true 為我方）。</summary>
		public static BoardSide ReadBoard(bool friendly)
		{
			try
			{
				var c = Client();
				if(c == null) return null;
				_board ??= c.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetBoardState");
				if(_board == null) return null;
				var args = _board.GetParameters().Select(p => p.ParameterType == typeof(bool) ? (object)true : null).ToArray();
				var st = _board.Invoke(c, args);
				var side = Get(st, friendly ? "Friendly" : "Opposing");
				if(!(Get(side, "BoardCards") is IEnumerable cards))
					return null;
				var list = cards.Cast<object>().ToList();
				var res = new BoardSide { Count = list.Count };
				var ids = new List<int>();
				foreach(var card in list)
				{
					var id = Get(card, "EntityId") ?? Get(card, "EntityID") ?? Get(card, "Id");
					if(id is int i) ids.Add(i);
				}
				if(ids.Count == list.Count && ids.Count > 0)
					res.EntityIds = ids;
				return res;
			}
			catch(Exception)
			{
				return null;
			}
		}
	}
}
