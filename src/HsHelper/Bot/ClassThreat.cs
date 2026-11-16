using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HsHelper.Bot
{
	/// <summary>
	/// 職業卡牌模組（v0.9.5）：每個職業的「全場傷害（AOE）」與「爆發（直傷、衝鋒）」牌，從全部卡牌讀出來預先整理好。
	/// 對局中確認對手職業後只載入那個職業的模組，用來預測對手下回合可能的清場與爆發：
	/// 鋪場時考慮會不會被一次清光、血量要留多少。對手出過 AOE 牌之後，機率會調高。
	/// </summary>
	public static class ClassThreat
	{
		[DataContract] public class Item { [DataMember] public string Id = ""; [DataMember] public int Cost; [DataMember] public int Dmg; }
		[DataContract] public class Module
		{
			[DataMember] public string Class = "";
			[DataMember] public List<Item> Aoe = new List<Item>();
			[DataMember] public List<Item> Burn = new List<Item>();
		}

		public class Threat
		{
			public int AoeDmg;          // 預估清場傷害（0 = 這個費用沒有清場牌）
			public double AoeChance;    // 對手手上有清場牌的機率估計
			public int Burst;           // 下回合單張最大爆發
			public string Summary = "";
		}

		private static Dictionary<string, Module> _modules;

		public static Dictionary<string, Module> Modules
		{
			get
			{
				if(_modules != null) return _modules;
				try
				{
					using var s = typeof(ClassThreat).Assembly.GetManifestResourceStream("HsHelper.class-threats.json");
					if(s != null) Load(s);
				}
				catch(Exception) { }
				return _modules ??= new Dictionary<string, Module>();
			}
		}

		public static void Load(Stream s)
		{
			var list = (List<Module>)new DataContractJsonSerializer(typeof(List<Module>)).ReadObject(s);
			_modules = list.ToDictionary(m => Norm(m.Class), m => m);
		}

		public static void LoadFile(string path)
		{
			using var fs = File.OpenRead(path);
			Load(fs);
		}

		public static string ToJson(List<Module> list)
		{
			using var ms = new MemoryStream();
			new DataContractJsonSerializer(typeof(List<Module>)).WriteObject(ms, list);
			return Encoding.UTF8.GetString(ms.ToArray());
		}

		/// <summary>測試用：加入／替換一個職業模組。</summary>
		public static void Set(Module m) => Modules[Norm(m.Class)] = m;

		public static string Norm(string c) => (c ?? "").Replace(" ", "").ToUpperInvariant();

		/// <summary>對手職業＋對手下回合水晶＋對手已出過的牌 → 預估威脅。</summary>
		public static Threat For(string oppClass, int oppMana, ICollection<string> seenCards)
		{
			var t = new Threat();
			if(string.IsNullOrEmpty(oppClass) || !Modules.TryGetValue(Norm(oppClass), out var m))
				return t;
			var seen = seenCards != null ? new HashSet<string>(seenCards) : new HashSet<string>();
			var aoe = m.Aoe.Where(x => x.Cost <= oppMana).Select(x => Math.Min(10, x.Dmg)).OrderBy(x => x).ToList();
			if(aoe.Count > 0)
			{
				t.AoeDmg = aoe[aoe.Count / 2]; // 中位數
				var shown = m.Aoe.Any(x => seen.Contains(x.Id));
				// 這個費用能用的清場牌越多，越可能帶；對手已經用過清場牌 → 這副牌確定有
				t.AoeChance = shown ? 0.55 : Math.Min(0.35, 0.12 + 0.02 * aoe.Count);
			}
			var burn = m.Burn.Where(x => x.Cost <= oppMana).Select(x => x.Dmg).DefaultIfEmpty(0).Max();
			t.Burst = burn;
			t.Summary = t.AoeDmg > 0 ? $"清場約 {t.AoeDmg} 傷（{t.AoeChance:P0}）、爆發 {t.Burst}" : $"爆發 {t.Burst}";
			return t;
		}

		/// <summary>把職業威脅套進權重：清場風險（鋪場時考慮）、爆發（血量要留多少）。只會讓 AI 更保守一點，不會蓋掉學習資料。</summary>
		public static Weights Apply(Weights w, Threat t)
		{
			if(t == null) return w;
			w = w.Clone();
			w.EnemyAoeDmg = t.AoeDmg;
			w.EnemyAoeChance = t.AoeChance;
			w.EnemyBurst = Math.Max(w.EnemyBurst, Math.Min(8, t.Burst * 0.5));
			return w;
		}
	}
}
