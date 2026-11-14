using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HsHelper
{
	public class MinionInfo
	{
		public string Name;
		public int Attack;
		public int Health;
		public int Tier;
	}

	/// <summary>從 HDT 讀到的遊戲狀態快照（與 UI 無關，之後的決策模組也會用這份資料）。</summary>
	public class GameSnapshot
	{
		public bool InGame;
		public bool IsBattlegrounds;
		public bool IsCombatPhase;
		public int Turn;

		public string PlayerClass;
		public int PlayerHealth;
		public int PlayerHand;
		public int PlayerDeck;
		public int TavernTier;
		public List<MinionInfo> PlayerBoard = new List<MinionInfo>();

		public string OpponentClass;
		public string OpponentName;
		public int OpponentHealth;
		public int OpponentHand;
		public int OpponentDeck;
		public int OpponentTavernTier;
		public List<MinionInfo> OpponentBoard = new List<MinionInfo>();
	}

	public static class GameStateReader
	{
		public static GameSnapshot Read()
		{
			var game = ApiCore.Game;
			var s = new GameSnapshot();
			if(game == null || !game.IsRunning || game.IsInMenu || game.Player == null || game.Opponent == null)
				return s;

			s.InGame = true;
			s.IsBattlegrounds = game.IsBattlegroundsMatch;
			s.IsCombatPhase = game.IsBattlegroundsCombatPhase;
			s.Turn = game.GetTurnNumber();

			var me = game.Player;
			var op = game.Opponent;

			s.PlayerClass = ClassName(me.CurrentClass);
			s.PlayerHealth = HeroHealth(me.Hero);
			s.PlayerHand = me.HandCount;
			s.PlayerDeck = me.DeckCount;
			s.TavernTier = me.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL) ?? 0;
			s.PlayerBoard = Minions(me);

			s.OpponentClass = ClassName(op.CurrentClass);
			s.OpponentName = op.Hero?.Card?.LocalizedName;
			s.OpponentHealth = HeroHealth(op.Hero);
			s.OpponentHand = op.HandCount;
			s.OpponentDeck = op.DeckCount;
			s.OpponentTavernTier = op.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL) ?? 0;
			s.OpponentBoard = Minions(op);
			return s;
		}

		private static readonly Dictionary<string, string> ClassNames = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
		{
			["Mage"] = "法師", ["Hunter"] = "獵人", ["Warrior"] = "戰士", ["Priest"] = "牧師",
			["Rogue"] = "盜賊", ["Shaman"] = "薩滿", ["Warlock"] = "術士", ["Paladin"] = "聖騎士",
			["Druid"] = "德魯伊", ["DemonHunter"] = "惡魔獵人", ["DeathKnight"] = "死亡騎士",
			["Neutral"] = "中立"
		};

		private static string ClassName(string cls)
		{
			if(string.IsNullOrEmpty(cls))
				return null;
			var key = cls.Replace("_", "").Replace(" ", "");
			return ClassNames.TryGetValue(key, out var zh) ? zh : cls;
		}

		private static int HeroHealth(Entity hero) =>
			hero == null ? 0 : hero.Health + hero.GetTag(GameTag.ARMOR);

		private static List<MinionInfo> Minions(Player p) =>
			p.Board
				.Where(e => e.IsMinion)
				.OrderBy(e => e.ZonePosition)
				.Select(e => new MinionInfo
				{
					Name = e.Card?.LocalizedName ?? e.CardId ?? "?",
					Attack = e.Attack,
					Health = e.Health,
					Tier = e.GetTag(GameTag.TECH_LEVEL)
				})
				.ToList();
	}
}
