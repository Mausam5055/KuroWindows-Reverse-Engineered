using System;
using System.Collections.Generic;

namespace KuroWindows.Input;

internal static class BrowserCatalog
{
	private static readonly HashSet<string> BrowserExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"arc.exe", "atlas.exe", "avastbrowser.exe", "avgbrowser.exe", "basilisk.exe", "baidubrowser.exe", "brave.exe", "browser.exe", "ccleanerbrowser.exe", "centbrowser.exe",
		"citrio.exe", "chrome.exe", "chrome_proxy.exe", "chromium.exe", "coccoc.exe", "comet.exe", "comodo_dragon.exe", "dooble.exe", "dragon.exe", "duckduckgo.exe",
		"duckduckgobrowser.exe", "epic.exe", "falkon.exe", "firefox.exe", "floorp.exe", "icedragon.exe", "iexplore.exe", "iridium.exe", "iron.exe", "k-meleon.exe",
		"librewolf.exe", "maxthon.exe", "mercury.exe", "midori.exe", "msedge.exe", "msedge_proxy.exe", "opera.exe", "opera_gx.exe", "palemoon.exe", "qqbrowser.exe",
		"qutebrowser.exe", "safari.exe", "seamonkey.exe", "sidekick.exe", "slimbrowser.exe", "slimjet.exe", "sogouexplorer.exe", "srware iron.exe", "sputnik.exe", "thebrowser.exe",
		"thorium.exe", "torch.exe", "tor.exe", "torbrowser.exe", "ucbrowser.exe", "vivaldi.exe", "waterfox.exe", "wavebox.exe", "whale.exe", "yandexbrowser.exe",
		"zen.exe"
	};

	private static readonly string[] BrowserTitleSuffixes = new string[60]
	{
		"Google Chrome", "Chrome", "Microsoft Edge", "Edge", "Mozilla Firefox", "Firefox Developer Edition", "Firefox Nightly", "Firefox", "Brave", "Opera GX",
		"Opera", "Vivaldi", "Arc", "Zen Browser", "Zen", "Chromium", "DuckDuckGo Browser", "DuckDuckGo", "Tor Browser", "Waterfox",
		"Comet", "Atlas", "Baidu Browser", "LibreWolf", "Floorp", "Mercury", "Pale Moon", "Basilisk", "SeaMonkey", "Maxthon",
		"Yandex Browser", "360 Secure Browser", "360 Browser", "QQ Browser", "Sogou Explorer", "Wavebox", "Sidekick", "Avast Secure Browser", "AVG Secure Browser", "CCleaner Browser",
		"Epic Privacy Browser", "SRWare Iron", "Iron", "Iridium", "Thorium", "Slimjet", "Cent Browser", "CocCoc", "Whale", "UC Browser",
		"Falkon", "qutebrowser", "Dooble", "Midori", "Internet Explorer", "Safari", "Comodo Dragon", "Comodo IceDragon", "Torch", "Sputnik"
	};

	private static readonly string[] TitleSeparators = new string[3] { " - ", " — ", " – " };

	public static bool IsBrowserExe(string? exeName)
	{
		if (!string.IsNullOrWhiteSpace(exeName))
		{
			return BrowserExes.Contains(exeName);
		}
		return false;
	}

	public static string CleanPageTitle(string rawTitle)
	{
		string text = rawTitle.Trim();
		if (text.Length == 0)
		{
			return "";
		}
		string[] browserTitleSuffixes = BrowserTitleSuffixes;
		foreach (string text2 in browserTitleSuffixes)
		{
			string[] titleSeparators = TitleSeparators;
			for (int j = 0; j < titleSeparators.Length; j++)
			{
				string text3 = titleSeparators[j] + text2;
				if (text.EndsWith(text3, StringComparison.OrdinalIgnoreCase))
				{
					string text4 = text;
					int length = text3.Length;
					return text4.Substring(0, text4.Length - length).Trim();
				}
			}
		}
		return text;
	}
}
