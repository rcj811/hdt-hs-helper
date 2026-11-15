using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Utility.Logging;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper.Bot
{
	/// <summary>
	/// 「組合（Assemble）」選擇畫面：左右兩張候選卡，各有上半／下半兩個選項，選好再按中間的「組合」。
	/// 狀態從遊戲記憶體讀（HearthMirror GetAssembleState，與 HDT 的 AssembleStateWatcher 同來源）；
	/// 位置沿用 HDT RegionDrawer.DrawAssembleScreenRegions 的版面。
	/// </summary>
	public static class Assemble
	{
		public class State
		{
			public bool Showing;
			public int Target;
			public int Selected;
			public List<int> Choices = new List<int>();
		}

		public class Option
		{
			public int EntityId;
			public int Index;   // 0 = 左邊那張，1 = 右邊那張
			public int Half;    // 1 = 上半，2 = 下半（與 HDT 的 HoveredHalf 相同定義）
			public string Name;
			public int Cost;
			public Point Screen;
		}

		private static MethodInfo _get;
		private static bool _unsupported;

		/// <summary>讀組合畫面狀態（用反射呼叫，舊版 HearthMirror 沒有這個方法時視為不支援）。</summary>
		public static State Read()
		{
			var st = new State();
			if(_unsupported)
				return st;
			try
			{
				var reflectionType = typeof(HearthMirror.Reflection);
				var client = reflectionType.GetProperty("Client", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
				if(client == null)
					return st;
				_get ??= client.GetType().GetMethod("GetAssembleState", System.Type.EmptyTypes);
				if(_get == null)
				{
					_unsupported = true;
					Log.Warn("HearthMirror has no GetAssembleState");
					return st;
				}
				var obj = _get.Invoke(client, null);
				if(obj == null)
					return st;
				var t = obj.GetType();
				T P<T>(string name)
				{
					var v = t.GetProperty(name)?.GetValue(obj) ?? t.GetField(name)?.GetValue(obj);
					return v is T x ? x : default;
				}
				st.Showing = !P<bool>("IsConcealed");
				st.Target = P<int?>("TargetEntityId") ?? 0;
				st.Selected = P<int?>("SelectedChoiceEntityId") ?? 0;
				if((t.GetProperty("ChoiceEntityIds")?.GetValue(obj) ?? t.GetField("ChoiceEntityIds")?.GetValue(obj)) is IEnumerable ids)
					st.Choices = ids.Cast<object>().Select(Convert.ToInt32).ToList();
				st.Showing = st.Showing && st.Choices.Count > 0;
			}
			catch(Exception ex)
			{
				Log.Warn("Assemble read failed: " + ex.Message);
			}
			return st;
		}

		/// <summary>把選項換成「哪一張、哪一半、螢幕位置」（UI 執行緒）。</summary>
		public static List<Option> Options(State st)
		{
			var list = new List<Option>();
			var ow = ApiCore.OverlayWindow;
			var game = ApiCore.Game;
			if(ow == null || game == null)
				return list;
			double W = ow.Width, H = ow.Height;
			var ratio = (4.0 / 3.0) / (W / H);
			double ScaledX(double left) => W * ratio * left + W * (1 - ratio) / 2;

			var entities = st.Choices.Select(id => game.Entities.TryGetValue(id, out var e) ? e : null).Where(e => e != null).ToList();
			foreach(var half in new[] { 1, 2 })
			{
				// HDT：上半 = 標記 CRAFTABLE_SECOND_HALF_DBID 為 1 的選項；下半 = 標記 FIRST_HALF 為 1
				var marker = half == 1 ? GameTag.CRAFTABLE_SECOND_HALF_DBID : GameTag.CRAFTABLE_FIRST_HALF_DBID;
				var ofHalf = entities.Where(e => e.GetTag(marker) == 1).ToList();
				for(var i = 0; i < ofHalf.Count && i < 2; i++)
				{
					// 左框 (0.082, 0.257, 寬 0.287H, 高 0.386H)；右框 (0.656, ...)
					var left = i == 0 ? 0.082 : 0.656;
					var x = ScaledX(left) + H * 0.287 / 2;
					var y = H * (0.257 + 0.386 * (half == 1 ? 0.30 : 0.72));
					list.Add(new Option
					{
						EntityId = ofHalf[i].Id, Index = i, Half = half,
						Name = ofHalf[i].Card?.LocalizedName ?? ofHalf[i].CardId, Cost = ofHalf[i].GetTag(GameTag.COST),
						Screen = ow.PointToScreen(new Point(x, y))
					});
				}
			}
			return list;
		}

		/// <summary>「組合」按鈕的候選位置（中間區塊下方，依序嘗試）。</summary>
		public static List<Point> ButtonPoints()
		{
			var ow = ApiCore.OverlayWindow;
			double W = ow.Width, H = ow.Height;
			var ratio = (4.0 / 3.0) / (W / H);
			var cx = W * ratio * 0.306 + W * (1 - ratio) / 2 + H * 0.456 / 2;
			return new[] { 0.72, 0.68, 0.755, 0.64 }.Select(y => ow.PointToScreen(new Point(cx, H * y))).ToList();
		}
	}
}
