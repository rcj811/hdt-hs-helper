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
