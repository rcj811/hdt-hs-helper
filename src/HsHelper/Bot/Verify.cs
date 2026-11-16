using System.Linq;

namespace HsHelper.Bot
{
	/// <summary>
	/// 判斷動作是否已經成功。HDT 讀遊戲 log 有延遲、召喚動畫也要時間，
	/// 所以不只看「牌離開手牌」，只要任一個跡象出現就算成功（v0.7.0 的報告顯示只看手牌會大量誤判）。
	/// </summary>
	public static class Verify
	{
		public static bool PlayResolved(Snapshot before, Snapshot after, Ent card)
		{
			if(after == null || !after.InGame || after.GameOver)
				return true;
			if(after.Hand.All(h => h.Id != card.Id))
				return true;                                   // 離開手牌
			if(after.MyBoard.Any(m => m.Id == card.Id))
				return true;                                   // 已經在場上（手牌資料還沒更新）
			if(card.Kind == Kind.Weapon && after.MyWeapon?.Id == card.Id)
				return true;                                   // 武器已裝備
			if(before != null && !string.IsNullOrEmpty(card.CardId)
			   && after.Hand.Count(h => h.CardId == card.CardId) < before.Hand.Count(h => h.CardId == card.CardId))
				return true;                                   // 同名牌少了一張（拖到的是另一張同名牌也算成功）
			if(card.Cost > 0 && before != null && after.Mana <= before.Mana - card.Cost)
				return true;                                   // 法力已扣
			return false;
		}

		public static bool HeroPowerResolved(Snapshot before, Snapshot after)
		{
			if(after == null || !after.InGame || after.GameOver)
				return true;
			if(after.MyHeroPower == null || after.MyHeroPower.Exhausted)
				return true;
			var cost = before?.MyHeroPower?.Cost ?? 0;
			return cost > 0 && before != null && after.Mana <= before.Mana - cost;
		}
	}
}
