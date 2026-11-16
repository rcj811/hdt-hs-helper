using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper.Bot
{
	/// <summary>
	/// 在 UI 執行緒上從 HDT 讀取遊戲狀態，並把每個物件換算成螢幕座標。
	/// 座標公式取自 HDT 自己的覆蓋層（手牌：GetPlayerCardPosition；手下：OverlayWindow 的板面排版）。
	/// </summary>
	public static class Snapshotter
	{
		public static Snapshot Take()
		{
			var s = new Snapshot();
			var game = ApiCore.Game;
			var ow = ApiCore.OverlayWindow;
			if(game == null || ow == null || !game.IsRunning || game.IsInMenu || game.Player == null || game.Opponent == null)
				return s;
			if(game.IsBattlegroundsMatch || game.IsMercenariesMatch)
				return s; // 自動出牌只支援構築／休閒／練習模式

			var gameEntity = game.GameEntity;
			var me = game.PlayerEntity;
			if(gameEntity == null || me == null)
				return s;

			s.InGame = true;
			s.GameOver = gameEntity.GetTag(GameTag.STATE) == (int)State.COMPLETE;
			var step = gameEntity.GetTag(GameTag.STEP);
			s.MainAction = step == (int)Step.MAIN_ACTION;
			s.MulliganInput = me.GetTag(GameTag.MULLIGAN_STATE) == (int)Mulligan.INPUT;
			s.MyTurn = me.GetTag(GameTag.CURRENT_PLAYER) == 1;
			s.Turn = game.GetTurnNumber();
			s.MyClass = game.Player.CurrentClass;
			s.OppClass = game.Opponent.CurrentClass;
			var deck = game.CurrentSelectedDeck;
			if(deck != null) { s.DeckId = deck.Id; s.DeckName = deck.Name ?? ""; }
			s.Deck = ReadDeck(game.Player);
			try { s.OppMana = game.OpponentEntity?.GetTag(GameTag.RESOURCES) ?? 0; } catch(Exception) { }
			var ps = me.GetTag(GameTag.PLAYSTATE);
			s.Won = ps == (int)PlayState.WON;
			try
			{
				s.OppCards = game.Opponent.RevealedEntities
					.Where(e => !string.IsNullOrEmpty(e.CardId) && !e.Info.Created)
					.Select(e => e.CardId).Distinct().ToList();
			}
			catch(Exception) { /* 讀不到就算了 */ }
			s.Mana = me.GetTag(GameTag.RESOURCES) + me.GetTag(GameTag.TEMP_RESOURCES)
			         - me.GetTag(GameTag.RESOURCES_USED) - me.GetTag(GameTag.OVERLOAD_LOCKED);
			s.ManaSpent = me.GetTag(GameTag.RESOURCES_USED);

			double W = ow.Width, H = ow.Height;
			if(double.IsNaN(W) || double.IsNaN(H) || W < 100 || H < 100)
				return new Snapshot();
			Point P(double x, double y) => ow.PointToScreen(new Point(x, y));

			// 手牌
			var hand = game.Player.Hand.OrderBy(e => e.ZonePosition).ToList();
			for(var i = 0; i < hand.Count; i++)
			{
				var e = Convert(hand[i]);
				var p = ow.GetPlayerCardPosition(i, hand.Count);
				e.Screen = P(p.X, p.Y);
				s.Hand.Add(e);
			}

			// 板面（HDT：MinionWidth = W*0.63/7*ScreenRatio，左右邊距 W*ScreenRatio*0.0029）
			var ratio = (4.0 / 3.0) / (W / H);
			var slot = W * 0.63 / 7 * ratio + 2 * W * ratio * 0.0029;
			var boardH = H * 0.158;
			var oppRowY = H / 2 - H * 0.045 - boardH / 2;
			var myRowY = H / 2 - H * 0.03 + boardH / 2;

			List<Ent> Board(IEnumerable<Entity> src, double y)
			{
				var list = src.Where(e => e.IsMinion || e.IsLocation).OrderBy(e => e.ZonePosition).ToList();
				var result = new List<Ent>();
				for(var i = 0; i < list.Count; i++)
				{
					var e = Convert(list[i]);
					e.Screen = P(W / 2 + (i - (list.Count - 1) / 2.0) * slot, y);
					result.Add(e);
				}
				return result;
			}
			s.MyBoard = Board(game.Player.Board, myRowY);
			s.OppBoard = Board(game.Opponent.Board, oppRowY);

			// 英雄、英雄技能、結束回合（依你截圖量出的相對位置，以畫面高度為單位）
			var myHero = game.Player.Board.FirstOrDefault(e => e.IsHero);
			var oppHero = game.Opponent.Board.FirstOrDefault(e => e.IsHero);
			var hp = game.Player.Board.FirstOrDefault(e => e.IsHeroPower);
			if(myHero != null) { s.MyHero = Convert(myHero); s.MyHero.Screen = P(W / 2, H * 0.766); }
			if(oppHero != null) { s.OppHero = Convert(oppHero); s.OppHero.Screen = P(W / 2, H * 0.182); }
			var weapon = game.Player.Board.FirstOrDefault(e => e.IsWeapon);
			if(weapon != null)
				s.MyWeapon = Convert(weapon);
			s.HasCoin = s.Hand.Any(c => c.IsCoin);
			if(hp != null)
			{
				s.MyHeroPower = Convert(hp, true);
				s.MyHeroPower.Screen = P(W / 2 + H * 0.164, H * 0.760);
			}

			s.EndTurn = P(W / 2 + H * 0.556, H * 0.454);
			s.BoardDrop = P(W / 2 + (s.MyBoard.Count / 2.0 + 0.5) * slot, myRowY);

			// 各種張數的排版（用遊戲記憶體讀到的即時張數來選，避免 HDT 資料延遲時位置算錯）
			for(var n = 1; n <= 10; n++)
			{
				var l = new List<Point>();
				for(var i = 0; i < n; i++)
				{
					var hp2 = ow.GetPlayerCardPosition(i, n);
					l.Add(P(hp2.X, hp2.Y));
				}
				s.HandLayouts[n] = l;
			}
			for(var n = 1; n <= 8; n++)
			{
				s.MyRow[n] = Enumerable.Range(0, n).Select(i => P(W / 2 + (i - (n - 1) / 2.0) * slot, myRowY)).ToList();
				s.OppRow[n] = Enumerable.Range(0, n).Select(i => P(W / 2 + (i - (n - 1) / 2.0) * slot, oppRowY)).ToList();
			}
			s.BoardCenter = P(W / 2 + H * 0.05, H * 0.50);
			s.Neutral = P(W / 2 + H * 0.45, H * 0.62);

			// 起手換牌：照 HDT 換牌指南的排版（1080 高度基準、寬 1020 平均分配）
			if(s.MulliganInput)
			{
				var n = hand.Count(e => !e.IsTheCoin);
				for(var i = 0; i < n; i++)
					s.MulliganCards.Add(P(W / 2 + H * 3 / 1080 + (i - (n - 1) / 2.0) * (1020.0 / n) / 1080 * H, H * 0.486));
				foreach(var y in new[] { 0.805, 0.835, 0.775 })
					s.MulliganConfirm.Add(P(W / 2, H * y));
			}

			// 發現／抉擇／黑暗之賜：位置取自 HDT RegionDrawer.DrawDiscoverCardRegions
			var offered = game.Player.OfferedEntities.OrderBy(e => e.ZonePosition).ToList();
			if(offered.Count > 0)
			{
				var darkGift = offered.Any(e => e.GetTag(GameTag.DARK_GIFT_ENTITY) > 0);
				s.OfferedDarkGift = darkGift;
				var spacing = darkGift ? 0.287 : 0.27;
				var center = darkGift ? 0.519 : 0.53;
				var top = darkGift ? 0.185 : 0.29;
				var cardH = darkGift ? 0.605 : 0.39;
				var cardW = darkGift ? 0.332 : 0.28; // 卡片寬（以畫面高度為單位）
				var leftEdge = center - offered.Count * spacing / 2;
				for(var i = 0; i < offered.Count; i++)
				{
					var e = Convert(offered[i]);
					var cardX = leftEdge + i * spacing;
					var x = W * ratio * cardX + W * (1 - ratio) / 2 + H * cardW / 2;
					e.Screen = P(x, H * (top + cardH * 0.45));
					s.Offered.Add(e);
				}
			}
			return s;
		}

		private static Ent Convert(Entity x, bool heroPower = false)
		{
			int T(GameTag t) => x.GetTag(t);
			var e = new Ent
			{
				Id = x.Id,
				CardId = x.CardId,
				Name = x.Card?.LocalizedName ?? x.CardId ?? "?",
				Kind = x.IsMinion ? Kind.Minion : x.IsSpell ? Kind.Spell : x.IsWeapon ? Kind.Weapon
					: x.IsHero ? Kind.Hero : x.IsHeroPower ? Kind.HeroPower : x.IsLocation ? Kind.Location : Kind.Other,
				Cost = T(GameTag.COST),
				Atk = T(GameTag.ATK),
				Hp = x.Health,
				Armor = T(GameTag.ARMOR),
				Taunt = T(GameTag.TAUNT) > 0,
				DivineShield = T(GameTag.DIVINE_SHIELD) > 0,
				Stealth = T(GameTag.STEALTH) > 0,
				Immune = T(GameTag.IMMUNE) > 0,
				Poisonous = T(GameTag.POISONOUS) > 0 || T(GameTag.VENOMOUS) > 0,
				Frozen = T(GameTag.FROZEN) > 0,
				Exhausted = T(GameTag.EXHAUSTED) > 0,
				Charge = T(GameTag.CHARGE) > 0,
				Rush = T(GameTag.RUSH) > 0,
				Windfury = T(GameTag.WINDFURY) > 0,
				MegaWindfury = T(GameTag.MEGA_WINDFURY) > 0,
				CantAttack = T(GameTag.CANT_ATTACK) > 0,
				Dormant = T(GameTag.DORMANT) > 0,
				Lifesteal = T(GameTag.LIFESTEAL) > 0,
				Untouchable = T(GameTag.UNTOUCHABLE) > 0,
				AttacksThisTurn = T(GameTag.NUM_ATTACKS_THIS_TURN),
				Durability = T(GameTag.DURABILITY) - (x.IsWeapon ? T(GameTag.DAMAGE) : 0),
				TurnsInPlay = T(GameTag.NUM_TURNS_IN_PLAY),
				IsCoin = x.IsTheCoin,
				Text = SafeText(x),
			};
			if(heroPower)
				e.Exhausted = e.Exhausted || T(GameTag.HERO_POWER_DISABLED) > 0;
			CardText.Classify(e, heroPower);
			return e;
		}

		// ───────── 牌庫剩下的牌（v0.9.5）─────────
		private static System.Reflection.MethodInfo _deckState;
		private static string _deckKey = "";
		private static List<Ent> _deckCache = new List<Ent>();

		/// <summary>讀 HDT 推算的「牌庫剩下的牌」（套牌清單扣掉已抽／已打出），讀不到回傳空清單。</summary>
		private static List<Ent> ReadDeck(Hearthstone_Deck_Tracker.Hearthstone.Player player)
		{
			try
			{
				_deckState ??= player.GetType().GetMethod("GetDeckState",
					System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
				var state = _deckState?.Invoke(player, null);
				var cards = state?.GetType().GetProperty("RemainingInDeck")?.GetValue(state) as IEnumerable<Hearthstone_Deck_Tracker.Hearthstone.Card>;
				if(cards == null)
					return new List<Ent>();
				var list = cards.Where(c => c != null && c.Count > 0).ToList();
				var key = string.Join(",", list.Select(c => c.Id + "x" + c.Count));
				if(key == _deckKey)
					return _deckCache;
				var result = new List<Ent>();
				foreach(var c in list)
				{
					var mech = c.Mechanics ?? new string[0];
					bool M(string m) => mech.Contains(m);
					for(var i = 0; i < c.Count; i++)
					{
						var e = new Ent
						{
							Id = -1, CardId = c.Id, Name = c.LocalizedName ?? c.Name ?? c.Id, Cost = c.Cost, Atk = c.Attack, Hp = c.Health,
							Kind = c.Type == "Minion" ? Kind.Minion : c.Type == "Spell" ? Kind.Spell : c.Type == "Weapon" ? Kind.Weapon : Kind.Other,
							Taunt = M("TAUNT"), Charge = M("CHARGE"), Rush = M("RUSH"), DivineShield = M("DIVINE_SHIELD"),
							Lifesteal = M("LIFESTEAL"), Poisonous = M("POISONOUS"), Windfury = M("WINDFURY"),
							Text = c.EnglishText ?? ""
						};
						CardText.Classify(e);
						result.Add(e);
					}
				}
				_deckKey = key;
				_deckCache = result;
				return result;
			}
			catch(Exception)
			{
				return new List<Ent>();
			}
		}

		private static string SafeText(Entity x)
		{
			try { return x.Card?.EnglishText ?? ""; }
			catch(Exception) { return ""; }
		}
	}
}
