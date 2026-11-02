using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KuroWindows.Input.Uia;
using KuroWindows.Sync;

namespace KuroWindows.Input;

internal static class UiaGuard
{
	private static readonly HashSet<string> BlockedWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty" };

	private static readonly HashSet<string> BlockedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "conhost.exe", "openconsole.exe", "windowsterminal.exe", "wt.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "mintty.exe", "alacritty.exe", "wezterm-gui.exe" };

	public static bool IsHostileWindow(nint hwnd, string? exeName, DesktopFeatureFlags flags)
	{
		if (hwnd == IntPtr.Zero)
		{
			return true;
		}
		string windowClassName = GetWindowClassName(hwnd);
		if (windowClassName.Length == 0)
		{
			return true;
		}
		return IsHostileIdentity(windowClassName, exeName, flags);
	}

	internal static bool IsHostileIdentity(string? className, string? exeName, DesktopFeatureFlags flags)
	{
		string[] uiaBlockedApps;
		if (!string.IsNullOrEmpty(exeName))
		{
			if (BlockedApps.Contains(exeName))
			{
				return true;
			}
			if (UiaProbeSuppression.IsSuppressed(exeName))
			{
				return true;
			}
			uiaBlockedApps = flags.UiaBlockedApps;
			for (int i = 0; i < uiaBlockedApps.Length; i++)
			{
				if (string.Equals(uiaBlockedApps[i], exeName, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}
		if (string.IsNullOrEmpty(className))
		{
			return false;
		}
		if (BlockedWindowClasses.Contains(className))
		{
			return true;
		}
		uiaBlockedApps = flags.UiaBlockedWindowClasses;
		for (int i = 0; i < uiaBlockedApps.Length; i++)
		{
			if (string.Equals(uiaBlockedApps[i], className, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	internal static string ClassNameOf(nint hwnd)
	{
		return GetWindowClassName(hwnd);
	}

	private static string GetWindowClassName(nint hwnd)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		if (GetClassName(hwnd, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return "";
		}
		return stringBuilder.ToString();
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int GetClassName(nint hwnd, StringBuilder lpClassName, int nMaxCount);
}
