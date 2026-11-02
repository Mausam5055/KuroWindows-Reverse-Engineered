using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeviceIdentity;

internal static class RemoteControlProbe
{
	public const int MaxTools = 8;

	private static readonly string[] Allowlist = new string[15]
	{
		"AnyDesk", "TeamViewer", "TeamViewer_Service", "rustdesk", "remoting_host", "parsecd", "SRService", "UltraViewer_Service", "dwagent", "vncserver",
		"tvnserver", "winvnc", "screensharingd", "RustDesk", "Jump Desktop Connect"
	};

	private static readonly Dictionary<string, string> ByLowercase = BuildIndex();

	private const int SmRemoteSession = 4096;

	public static bool IsRemoteSession => GetSystemMetrics(4096) != 0;

	private static Dictionary<string, string> BuildIndex()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
		string[] allowlist = Allowlist;
		foreach (string text in allowlist)
		{
			dictionary.TryAdd(text.ToLowerInvariant(), text);
		}
		return dictionary;
	}

	public static IReadOnlyList<string> Scan()
	{
		List<string> list = new List<string>(8);
		HashSet<string> seenLower = new HashSet<string>(StringComparer.Ordinal);
		ScanProcesses(list, seenLower);
		if (list.Count < 8)
		{
			ScanServices(list, seenLower);
		}
		return list;
	}

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	private static void ScanProcesses(List<string> seen, HashSet<string> seenLower)
	{
		Process[] processes;
		try
		{
			processes = Process.GetProcesses();
		}
		catch
		{
			return;
		}
		Process[] array = processes;
		foreach (Process process in array)
		{
			try
			{
				if (seen.Count >= 8)
				{
					break;
				}
				Add(process.ProcessName, seen, seenLower);
			}
			catch
			{
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	private static void ScanServices(List<string> seen, HashSet<string> seenLower)
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services", writable: false);
			if (registryKey == null)
			{
				return;
			}
			string[] subKeyNames = registryKey.GetSubKeyNames();
			foreach (string candidate in subKeyNames)
			{
				if (seen.Count < 8)
				{
					Add(candidate, seen, seenLower);
					continue;
				}
				break;
			}
		}
		catch
		{
		}
	}

	private static void Add(string? candidate, List<string> seen, HashSet<string> seenLower)
	{
		if (!string.IsNullOrWhiteSpace(candidate))
		{
			string text = candidate.Trim().ToLowerInvariant();
			if (text.EndsWith(".exe", StringComparison.Ordinal))
			{
				string text2 = text;
				text = text2.Substring(0, text2.Length - 4);
			}
			if (ByLowercase.TryGetValue(text, out string value) && seenLower.Add(value.ToLowerInvariant()))
			{
				seen.Add(value);
			}
		}
	}
}
