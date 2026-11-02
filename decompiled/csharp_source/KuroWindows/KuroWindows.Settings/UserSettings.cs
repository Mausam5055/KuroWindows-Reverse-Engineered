using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using KuroWindows.Auth;

namespace KuroWindows.Settings;

public class UserSettings
{
	private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "settings.json");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private string[] _dismissedNoticeIds = Array.Empty<string>();

	private PendingConsent[] _pendingConsents = Array.Empty<PendingConsent>();

	private static readonly object SaveLock = new object();

	public static UserSettings Current { get; private set; } = new UserSettings();

	[JsonPropertyName("onboardingCompleted")]
	public bool OnboardingCompleted { get; set; }

	[JsonPropertyName("recordingEnabled")]
	public bool RecordingEnabled { get; set; } = true;

	[JsonPropertyName("launchAtStartup")]
	public bool LaunchAtStartup { get; set; } = true;

	[JsonPropertyName("theme")]
	public string Theme { get; set; } = "light";

	[JsonPropertyName("dismissedNoticeIds")]
	public string[] DismissedNoticeIds
	{
		get
		{
			return Volatile.Read(in _dismissedNoticeIds);
		}
		set
		{
			Volatile.Write(ref _dismissedNoticeIds, value ?? Array.Empty<string>());
		}
	}

	[JsonPropertyName("pendingConsents")]
	public PendingConsent[] PendingConsents
	{
		get
		{
			return Volatile.Read(in _pendingConsents);
		}
		set
		{
			Volatile.Write(ref _pendingConsents, value ?? Array.Empty<PendingConsent>());
		}
	}

	public string[] AddDismissedNotice(string id)
	{
		string[] array;
		string[] array2;
		do
		{
			array = Volatile.Read(in _dismissedNoticeIds);
			if (Array.IndexOf(array, id) >= 0)
			{
				return array;
			}
			array2 = new string[array.Length + 1];
			Array.Copy(array, array2, array.Length);
			array2[array.Length] = id;
		}
		while (Interlocked.CompareExchange(ref _dismissedNoticeIds, array2, array) != array);
		return array2;
	}

	public string[] PruneDismissedNotices(IReadOnlyCollection<string> feedIds)
	{
		string[] array;
		string[] array2;
		do
		{
			array = Volatile.Read(in _dismissedNoticeIds);
			array2 = array.Where(feedIds.Contains).ToArray();
			if (array2.Length == array.Length)
			{
				return array;
			}
		}
		while (Interlocked.CompareExchange(ref _dismissedNoticeIds, array2, array) != array);
		return array2;
	}

	public PendingConsent[] AddPendingConsents(IEnumerable<PendingConsent> entries)
	{
		PendingConsent[] array;
		while (true)
		{
			PendingConsent[] current = Volatile.Read(in _pendingConsents);
			array = current.Concat(entries.Where((PendingConsent e) => !Enumerable.Contains(current, e))).Distinct().ToArray();
			if (array.Length == current.Length)
			{
				return current;
			}
			if (Interlocked.CompareExchange(ref _pendingConsents, array, current) == current)
			{
				break;
			}
		}
		return array;
	}

	public PendingConsent[] RemovePendingConsent(PendingConsent entry)
	{
		PendingConsent[] array;
		PendingConsent[] array2;
		do
		{
			array = Volatile.Read(in _pendingConsents);
			array2 = array.Where((PendingConsent e) => e != entry).ToArray();
			if (array2.Length == array.Length)
			{
				return array;
			}
		}
		while (Interlocked.CompareExchange(ref _pendingConsents, array2, array) != array);
		return array2;
	}

	public static void Load()
	{
		try
		{
			if (File.Exists(SettingsPath))
			{
				Current = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new UserSettings();
			}
		}
		catch (Exception ex)
		{
			Current = new UserSettings();
			KuroLog.App($"UserSettings load failed; reverting to defaults (onboarding+recording flags reset) type={ex.GetType().Name} hr=0x{ex.HResult:X8}: {ex.Message}", "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\UserSettings.cs", 153);
		}
	}

	public static void Save()
	{
		try
		{
			lock (SaveLock)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
				string contents = JsonSerializer.Serialize(Current, JsonOptions);
				string text = SettingsPath + "." + Environment.ProcessId + ".tmp";
				File.WriteAllText(text, contents);
				File.Move(text, SettingsPath, overwrite: true);
			}
		}
		catch (Exception ex)
		{
			KuroLog.App($"UserSettings save failed type={ex.GetType().Name} hr=0x{ex.HResult:X8}: {ex.Message}", "Save", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Settings\\UserSettings.cs", 179);
		}
	}
}
