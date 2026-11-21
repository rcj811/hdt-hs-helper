using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HsHelper
{
	/// <summary>
	/// 檢查 GitHub 上有沒有新版（v0.9.9：實戰報告發現還在用兩版前的 0.9.6）。
	/// 只讀公開的 releases/latest，不送出任何個人資料。
	/// </summary>
	public static class UpdateCheck
	{
		public const string ReleasesPage = "https://github.com/rcj811/hdt-hs-helper/releases/latest";
		private const string Api = "https://api.github.com/repos/rcj811/hdt-hs-helper/releases/latest";

		public static string LatestTag { get; private set; } = "";

		/// <summary>回傳最新版的 tag（例如 v0.9.9）；比目前新才回傳，否則 null。</summary>
		public static Task<string> NewerAsync(Version current) => Task.Run(() =>
		{
			try
			{
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
				using var wc = new WebClient();
				wc.Headers[HttpRequestHeader.UserAgent] = "HsHelper-update-check";
				var json = wc.DownloadString(Api);
				var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
				if(!m.Success) return null;
				LatestTag = "v" + m.Groups[1].Value;
				return IsNewer(m.Groups[1].Value, current) ? LatestTag : null;
			}
			catch(Exception)
			{
				return null; // 沒網路或被擋：不影響使用
			}
		});

		public static bool IsNewer(string tag, Version current)
		{
			if(!Version.TryParse(tag.TrimStart('v'), out var v)) return false;
			var a = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
			var b = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
			return a > b;
		}
	}
}
