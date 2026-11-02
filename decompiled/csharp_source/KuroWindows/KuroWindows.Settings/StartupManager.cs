using System;
using Microsoft.Win32;

namespace KuroWindows.Settings;

public static class StartupManager
{
	private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string BackgroundLaunchArg = "--background";

	public static StartupRegistrationStatus SyncRegistration()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true) ?? Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (registryKey == null)
			{
				KuroLog.App("Startup registration sync failed: Could not open the Windows startup registry key.", "SyncRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\StartupManager.cs", 24);
				return GetRegistrationStatus("Could not open the Windows startup registry key.");
			}
			string text = registryKey.GetValue(AppConfig.StartupRunValueName) as string;
			if (!UserSettings.Current.OnboardingCompleted || !UserSettings.Current.LaunchAtStartup)
			{
				registryKey.DeleteValue(AppConfig.StartupRunValueName, throwOnMissingValue: false);
				if (text != null)
				{
					KuroLog.App("Startup registration removed (launch-at-startup disabled or onboarding incomplete)", "SyncRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\StartupManager.cs", 34);
				}
				return GetRegistrationStatus();
			}
			string text2 = ExpectedCommand();
			if (string.IsNullOrWhiteSpace(text2))
			{
				KuroLog.App("Startup registration sync failed: Could not resolve the current Kuro executable path.", "SyncRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\StartupManager.cs", 42);
				return GetRegistrationStatus("Could not resolve the current Kuro executable path.");
			}
			registryKey.SetValue(AppConfig.StartupRunValueName, text2, RegistryValueKind.String);
			if (!string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
			{
				KuroLog.App("Startup registration written for current executable", "SyncRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\StartupManager.cs", 49);
			}
			return GetRegistrationStatus();
		}
		catch (Exception ex)
		{
			KuroLog.App("Startup registration sync failed: " + ex.Message, "SyncRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\StartupManager.cs", 55);
			return GetRegistrationStatus(ex.Message);
		}
	}

	public static void RemoveRegistration()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			registryKey?.DeleteValue(AppConfig.StartupRunValueName, throwOnMissingValue: false);
		}
		catch (Exception ex)
		{
			KuroLog.App("Startup registration removal failed: " + ex.Message, "RemoveRegistration", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\StartupManager.cs", 80);
		}
	}

	public static StartupRegistrationStatus GetRegistrationStatus(string? error = null)
	{
		bool desiredEnabled = UserSettings.Current.OnboardingCompleted && UserSettings.Current.LaunchAtStartup;
		string text = ExpectedCommand();
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
			string text2 = registryKey?.GetValue(AppConfig.StartupRunValueName) as string;
			bool isRegistered = !string.IsNullOrWhiteSpace(text) && string.Equals(text2, text, StringComparison.OrdinalIgnoreCase);
			return new StartupRegistrationStatus(desiredEnabled, isRegistered, text2, text, error);
		}
		catch (Exception ex)
		{
			return new StartupRegistrationStatus(desiredEnabled, IsRegistered: false, null, text, error ?? ex.Message);
		}
	}

	private static string? ExpectedCommand()
	{
		string processPath = Environment.ProcessPath;
		if (!string.IsNullOrWhiteSpace(processPath))
		{
			return "\"" + processPath + "\" --background";
		}
		return null;
	}
}
