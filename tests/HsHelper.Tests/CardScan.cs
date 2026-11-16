using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HsHelper.Bot;

namespace HsHelper.Tests
{
	/// <summary>開發用：用遊戲全部卡牌（hsdata CardDefs.xml）檢查目標判斷，列出看不懂的牌。</summary>
	public static class CardScan
	{
		public static void Run(string path)
		{
			var doc = XDocument.Load(path);
			var stats = new Dictionary<TargetKind, int>();
			var unknown = new List<string>();
			var samples = new Dictionary<TargetKind, List<string>>();
			foreach(var e in doc.Root.Elements("Entity"))
			{
				string Tag(int id) => e.Elements("Tag").FirstOrDefault(t => (string)t.Attribute("enumID") == id.ToString())?.Element("enUS")?.Value;
				int TagV(int id) => (int?)e.Elements("Tag").FirstOrDefault(t => (string)t.Attribute("enumID") == id.ToString())?.Attribute("value") ?? 0;
				if(TagV(321) != 1) continue;                 // 只看可收藏
				var type = TagV(202);                        // 4 手下、5 法術
				if(type != 4 && type != 5) continue;
				var text = Tag(184) ?? "";
				var ent = new Ent { Text = text, Kind = type == 4 ? Kind.Minion : Kind.Spell };
				CardText.Classify(ent);
				stats[ent.Target] = stats.TryGetValue(ent.Target, out var n) ? n + 1 : 1;
				var line = $"{Tag(185)}: {Regex.Replace(ent.Text, @"\s+", " ")}";
				if(ent.Target == TargetKind.Unknown) unknown.Add(line);
				if(!samples.ContainsKey(ent.Target)) samples[ent.Target] = new List<string>();
				samples[ent.Target].Add(line);
			}
			foreach(var kv in stats.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Key}: {kv.Value}");
			Console.WriteLine("--- Unknown 範例 ---");
			foreach(var u in unknown.Take(40)) Console.WriteLine(u);
			var r = new Random(1);
			foreach(var k in new[] { TargetKind.FriendlyAny, TargetKind.FriendlyMinion, TargetKind.EnemyMinion, TargetKind.EnemyAny })
			{
				Console.WriteLine($"--- {k} 隨機 12 張 ---");
				foreach(var x in samples[k].OrderBy(_ => r.Next()).Take(12)) Console.WriteLine(x.Length > 150 ? x.Substring(0, 150) : x);
			}
		}
	}
}
namespace HsHelper.Tests
{
	public static class ZeroScan
	{
		public static void Run(string path)
		{
			var doc = System.Xml.Linq.XDocument.Load(path);
			foreach(var e in doc.Root.Elements("Entity"))
			{
				System.Xml.Linq.XElement T(int id) => System.Linq.Enumerable.FirstOrDefault(e.Elements("Tag"), t => (string)t.Attribute("enumID") == id.ToString());
				int V(int id) => (int?)T(id)?.Attribute("value") ?? 0;
				if(V(321) != 1 || V(48) != 0) continue;
				var type = V(202);
				if(type != 4 && type != 5 && type != 7 && type != 39) continue;
				var text = T(184)?.Element("enUS")?.Value ?? "";
				var ent = new HsHelper.Bot.Ent { Text = text, Kind = type == 4 ? HsHelper.Bot.Kind.Minion : HsHelper.Bot.Kind.Spell };
				HsHelper.Bot.CardText.Classify(ent);
				string clean(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"<[^>]+>|\s+|[\[\]$#]", " ").Trim();
				System.Console.WriteLine($"{type}\t{ent.Target}\t{T(185)?.Element("zhTW")?.Value}\t{T(185)?.Element("enUS")?.Value}\t{clean(text)}\t{clean(T(184)?.Element("zhTW")?.Value)}");
			}
		}
	}
}

namespace HsHelper.Tests
{
	/// <summary>全卡效果表：每張可收藏卡 AI 認出的能力（docs/card-effects.csv）＋辨識率統計。</summary>
	public static class EffectScan
	{
		public static void Run(string path, string outCsv)
		{
			var doc = System.Xml.Linq.XDocument.Load(path);
			var sb = new System.Text.StringBuilder("\uFEFFCardId,中文名,英文名,類型,費用,AI 認出的效果,敘述\n");
			int total = 0, withText = 0, recognized = 0, keywordOnly = 0;
			var seen = new System.Collections.Generic.HashSet<string>();
			foreach(var e in doc.Root.Elements("Entity"))
			{
				System.Xml.Linq.XElement T(int id) => System.Linq.Enumerable.FirstOrDefault(e.Elements("Tag"), t => (string)t.Attribute("enumID") == id.ToString());
				int V(int id) => (int?)T(id)?.Attribute("value") ?? 0;
				if(V(321) != 1) continue;
				var type = V(202);
				if(type != 4 && type != 5 && type != 7) continue;
				var en = T(185)?.Element("enUS")?.Value ?? "";
				var text = T(184)?.Element("enUS")?.Value ?? "";
				if(!seen.Add(en + "|" + text)) continue;
				total++;
				var ent = new HsHelper.Bot.Ent { Text = text, Cost = V(48), Atk = V(47), Hp = V(45),
					Kind = type == 4 ? HsHelper.Bot.Kind.Minion : type == 5 ? HsHelper.Bot.Kind.Spell : HsHelper.Bot.Kind.Weapon,
					Taunt = V(190) > 0, Charge = V(197) > 0, Rush = V(791) > 0, DivineShield = V(194) > 0, Lifesteal = V(1085) > 0, Poisonous = V(363) > 0, Windfury = V(189) > 0 };
				HsHelper.Bot.CardText.Classify(ent);
				var tags = HsHelper.Bot.CardText.Tags(ent);
				var scope = HsHelper.Bot.CardText.PlayScope(ent);
				var hasPlayText = scope.Length > 0;
				if(hasPlayText) withText++;
				var effectTags = tags.Where(x => !new[] { "嘲諷", "衝鋒", "突襲", "聖盾", "吸血", "劇毒", "風怒", "目標看不懂" }.Contains(x)).ToList();
				if(hasPlayText && effectTags.Count > 0) recognized++;
				if(!hasPlayText && tags.Count > 0) keywordOnly++;
				string q(string x) => "\"" + (x ?? "").Replace("\"", "'") + "\"";
				var zh = System.Text.RegularExpressions.Regex.Replace(T(184)?.Element("zhTW")?.Value ?? "", @"<[^>]+>|\s+|[\[\]$#]", " ").Trim();
				sb.Append($"{T(185)?.Parent?.Attribute("CardID")?.Value},{q(T(185)?.Element("zhTW")?.Value)},{q(en)},{(type == 4 ? "手下" : type == 5 ? "法術" : "武器")},{V(48)},{q(string.Join(" ", tags))},{q(zh)}\n");
			}
			System.IO.File.WriteAllText(outCsv, sb.ToString(), new System.Text.UTF8Encoding(false));
			System.Console.WriteLine($"可收藏卡（去重）：{total}；出牌時有效果文字：{withText}；其中認出至少一種效果：{recognized}（{recognized * 100.0 / System.Math.Max(1, withText):0.#}%）；純關鍵字手下：{keywordOnly}");
		}
	}
}

namespace HsHelper.Tests
{
	/// <summary>產生職業卡牌模組（src/HsHelper/Resources/class-threats.json）：每個職業的清場牌與爆發牌。</summary>
	public static class ClassThreatGen
	{
		private static readonly System.Collections.Generic.Dictionary<int, string> Names = new System.Collections.Generic.Dictionary<int, string>
		{
			[1] = "DeathKnight", [2] = "Druid", [3] = "Hunter", [4] = "Mage", [5] = "Paladin", [6] = "Priest",
			[7] = "Rogue", [8] = "Shaman", [9] = "Warlock", [10] = "Warrior", [14] = "DemonHunter"
		};

		public static void Run(string path, string outJson)
		{
			var doc = System.Xml.Linq.XDocument.Load(path);
			var mods = Names.ToDictionary(k => k.Key, k => new HsHelper.Bot.ClassThreat.Module { Class = k.Value });
			foreach(var e in doc.Root.Elements("Entity"))
			{
				System.Xml.Linq.XElement T(int id) => System.Linq.Enumerable.FirstOrDefault(e.Elements("Tag"), t => (string)t.Attribute("enumID") == id.ToString());
				int V(int id) => (int?)T(id)?.Attribute("value") ?? 0;
				if(V(321) != 1) continue;
				var type = V(202);
				if(type != 4 && type != 5) continue;
				if(!mods.TryGetValue(V(199), out var mod)) continue;
				var ent = new HsHelper.Bot.Ent { Text = T(184)?.Element("enUS")?.Value ?? "", Cost = V(48), Atk = V(47), Hp = V(45),
					Kind = type == 4 ? HsHelper.Bot.Kind.Minion : HsHelper.Bot.Kind.Spell, Charge = V(197) > 0 };
				HsHelper.Bot.CardText.Classify(ent);
				var id = (string)e.Attribute("CardID");
				var scope = HsHelper.Bot.CardText.PlayScope(ent).ToLowerInvariant();
				if(ent.AoE && !scope.Contains("friendly minions") && !scope.Contains("your minions"))
					mod.Aoe.Add(new HsHelper.Bot.ClassThreat.Item { Id = id, Cost = ent.Cost, Dmg = ent.Damage > 0 ? ent.Damage : 10 });
				var burn = ent.FaceDmg + (ent.Kind == HsHelper.Bot.Kind.Spell && ent.Target == HsHelper.Bot.TargetKind.EnemyAny ? ent.Damage : 0)
				           + (ent.Kind == HsHelper.Bot.Kind.Minion && ent.Charge ? ent.Atk : 0) + ent.HeroAtk;
				if(burn > 0)
					mod.Burn.Add(new HsHelper.Bot.ClassThreat.Item { Id = id, Cost = ent.Cost, Dmg = burn });
			}
			var list = mods.Values.ToList();
			System.IO.File.WriteAllText(outJson, HsHelper.Bot.ClassThreat.ToJson(list), new System.Text.UTF8Encoding(false));
			foreach(var m in list)
			{
				var a4 = m.Aoe.Where(x => x.Cost <= 4).Select(x => x.Dmg).OrderBy(x => x).ToList();
				System.Console.WriteLine($"{m.Class,-12} 清場 {m.Aoe.Count,3} 張（4 費內 {a4.Count}，中位 {(a4.Count > 0 ? a4[a4.Count / 2] : 0)} 傷）  爆發 {m.Burn.Count,3} 張");
			}
		}
	}
}
