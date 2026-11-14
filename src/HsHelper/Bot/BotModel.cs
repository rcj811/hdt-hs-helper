using System.Collections.Generic;
using System.Windows;

namespace HsHelper.Bot
{
	public enum Kind { Minion, Spell, Weapon, Hero, HeroPower, Location, Other }

	/// <summary>目標類型（從卡牌英文敘述推斷）。</summary>
	public enum TargetKind
	{
		None,           // 不需要目標
		EnemyAny,       // 傷害類：敵方角色（英雄或手下）
		EnemyMinion,    // 消滅／變形／沉默／冰凍類：敵方手下
		FriendlyMinion, // 增益類：友方手下
		FriendlyAny,    // 治療類：友方角色
		Unknown         // 需要目標但看不懂，跳過
	}

	/// <summary>一個實體（卡牌／手下／英雄）的快照，附帶它在螢幕上的座標。</summary>
	public class Ent
	{
		public int Id;
		public string Name;
		public string CardId;
		public Kind Kind;
		public int Cost;
		public int Atk;
		public int Hp;
		public int Armor;
		public bool Taunt, DivineShield, Stealth, Immune, Poisonous, Frozen, Exhausted;
		public bool Charge, Rush, Windfury, MegaWindfury, CantAttack, Dormant, Lifesteal, Untouchable;
		public int AttacksThisTurn;
		public int Durability;
		public int TurnsInPlay;
		public bool IsCoin;
		public string Text;          // 英文敘述（用來判斷目標）
		public TargetKind Target;
		public int Damage;            // 傷害類法術的傷害值（看不出來為 0）
		public bool AoE;              // 會打到全場
		public Point Screen;          // 螢幕座標（實體像素）

		public int MaxAttacks => MegaWindfury ? 4 : Windfury ? 2 : 1;

		public double Value =>
			Atk + Hp * 0.8 + (Taunt ? 1 : 0) + (DivineShield ? Atk * 0.5 + 1 : 0) + (Poisonous ? 2 : 0)
			+ (Lifesteal ? 1 : 0) + (Windfury ? Atk * 0.5 : 0);

		public override string ToString() => $"{Name}({Atk}/{Hp})";
	}

	public class Snapshot
	{
		public bool InGame;
		public bool GameOver;
		public bool MulliganInput;
		public bool MyTurn;
		public bool MainAction;
		public int Turn;
		public int Mana;
		public string MyClass;

		public List<Ent> Hand = new List<Ent>();
		public List<Ent> MyBoard = new List<Ent>();
		public List<Ent> OppBoard = new List<Ent>();
		public List<Ent> Offered = new List<Ent>();
		public Ent MyHero, OppHero, MyHeroPower, MyWeapon;
		public bool HasCoin;
		public bool Won;                                  // 對局結束時：我贏了
		public string OppClass;
		public List<string> OppCards = new List<string>(); // 對手出過的牌（學習用的牌組特徵）

		// 螢幕上的固定位置
		public Point EndTurn;
		public Point BoardDrop;      // 放手下的位置（我方板面最右邊）
		public Point BoardCenter;    // 施放不需目標法術的位置
		public List<Point> MulliganCards = new List<Point>();
		public List<Point> MulliganConfirm = new List<Point>();
		public Point Neutral;        // 安全的空白處（動作後把滑鼠移開）
	}
}
