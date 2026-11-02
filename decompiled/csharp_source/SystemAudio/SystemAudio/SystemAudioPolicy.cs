using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SystemAudio;

public static class SystemAudioPolicy
{
	private sealed record LatchFile([property: JsonPropertyName("failures")] int Failures, [property: JsonPropertyName("last_reason")] string? LastReason, [property: JsonPropertyName("app_version")] string? AppVersion, [property: JsonPropertyName("os_version")] string? OsVersion, [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt);

	public const string SourceLatch = "latch";

	public const string SourceEnv = "env";

	public const string SourceServed = "served";

	public const string SourceDefault = "default";

	private const int Absent = -1;

	private const int Off = 0;

	private const int On = 1;

	private static int _served = -1;

	private static int _lastAppliedServed = -1;

	private static int _lastAppliedEnv = -1;

	private static int _env = -1;

	private static int _unparseableLogged;

	internal const int LatchFailures = 3;

	private static readonly object LatchSync = new object();

	private static volatile bool _latchLoaded;

	private static int _failures;

	private static volatile string? _lastReason;

	internal static string? LatchPathOverride { get; set; }

	private static string LatchPath => LatchPathOverride ?? Path.Combine(Settings.LatchDirectory, Settings.LatchFileName);

	public static bool IsLatched
	{
		get
		{
			EnsureLatchLoaded();
			return Volatile.Read(in _failures) >= 3;
		}
	}

	public static void Apply(bool? served)
	{
		int value = ToState(served);
		Volatile.Write(ref _served, value);
		LogAppliedChange(ref _lastAppliedServed, value, "served");
	}

	public static void Clear(string reason)
	{
		Volatile.Write(ref _served, -1);
		if (Interlocked.Exchange(ref _lastAppliedServed, -1) == 1)
		{
			Log.Capture("system audio policy cleared reason=" + reason, "Clear", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 50);
		}
	}

	internal static void ApplyEnvironment(string? raw)
	{
		int value = ToState(ParseEnvironment(raw));
		Volatile.Write(ref _env, value);
		LogAppliedChange(ref _lastAppliedEnv, value, "env");
	}

	public static (bool Enabled, string Source) SnapshotForOpen()
	{
		int num = Volatile.Read(in _env);
		if (num != -1)
		{
			return (Enabled: num == 1, Source: "env");
		}
		if (IsLatched)
		{
			return (Enabled: false, Source: "latch");
		}
		int num2 = Volatile.Read(in _served);
		if (num2 == -1)
		{
			return (Enabled: false, Source: "default");
		}
		return (Enabled: num2 == 1, Source: "served");
	}

	public static string Describe()
	{
		var (flag, text) = SnapshotForOpen();
		return (flag ? "on" : "off") + ":" + text;
	}

	internal static bool PreviewEnabled()
	{
		int num = Volatile.Read(in _env);
		if (num != -1)
		{
			return num == 1;
		}
		return Volatile.Read(in _served) == 1;
	}

	internal static bool? ParseEnvironment(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}
		switch (raw.Trim().ToLowerInvariant())
		{
		case "1":
		case "true":
		case "on":
			return true;
		case "0":
		case "false":
		case "off":
			return false;
		default:
			if (Interlocked.Exchange(ref _unparseableLogged, 1) == 0)
			{
				Log.Capture("system audio env seam unparseable; ignoring value=" + raw, "ParseEnvironment", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 128);
			}
			return null;
		}
	}

	private static int ToState(bool? value)
	{
		if (value.HasValue)
		{
			if (value == true)
			{
				return 1;
			}
			return 0;
		}
		return -1;
	}

	private static void LogAppliedChange(ref int last, int value, string source)
	{
		int num = Interlocked.Exchange(ref last, value);
		bool flag = value == 1;
		if (num == 1 != flag)
		{
			Log.Capture("system audio policy applied value=" + (flag ? "true" : "false") + " source=" + source, "LogAppliedChange", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 150);
		}
	}

	internal static void RecordSegmentOutcome(bool succeeded, string? reason)
	{
		if (!succeeded && !IsCountedReason(reason))
		{
			return;
		}
		lock (LatchSync)
		{
			EnsureLatchLoadedLocked();
			int failures = _failures;
			int num = ((!succeeded) ? (failures + 1) : 0);
			if (num == failures)
			{
				return;
			}
			_failures = num;
			_lastReason = (succeeded ? null : reason);
			Persist();
			if (num < 3 || failures >= 3)
			{
				return;
			}
			Log.Capture($"system audio latched off failures={num} last={reason} app={SafeAppVersion()} os={SafeOsVersion()}", "RecordSegmentOutcome", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 211);
			try
			{
				SystemAudioRuntime.Latched?.Invoke();
			}
			catch
			{
			}
		}
	}

	internal static void ForgetLatch(string reason)
	{
		lock (LatchSync)
		{
			try
			{
				File.Delete(LatchPath);
				Log.Local("system audio latch forgotten reason=" + reason, "ForgetLatch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 231);
			}
			catch (Exception ex)
			{
				Log.Local("system audio latch forget failed: " + ex.Message, "ForgetLatch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 235);
			}
			_failures = 0;
			_lastReason = null;
			_latchLoaded = false;
		}
	}

	private static bool IsCountedReason(string? reason)
	{
		if (reason != null)
		{
			if (!reason.StartsWith("encoder_open_failed:", StringComparison.Ordinal) && !reason.StartsWith("finalize_failed:", StringComparison.Ordinal) && !reason.StartsWith("write_failed:", StringComparison.Ordinal) && (!reason.StartsWith("capture_", StringComparison.Ordinal) || reason.StartsWith("capture_format:", StringComparison.Ordinal)))
			{
				return reason == "finalize_timeout";
			}
			return true;
		}
		return false;
	}

	private static void Persist()
	{
		try
		{
			string latchPath = LatchPath;
			string directoryName = Path.GetDirectoryName(latchPath);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(latchPath, JsonSerializer.Serialize(new LatchFile(_failures, _lastReason, Settings.AppVersion, Settings.OsVersion, DateTimeOffset.UtcNow)));
		}
		catch (Exception ex)
		{
			Log.Capture("system audio latch persist failed: " + ex.Message, "Persist", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 272);
		}
	}

	private static void EnsureLatchLoaded()
	{
		if (_latchLoaded)
		{
			return;
		}
		lock (LatchSync)
		{
			EnsureLatchLoadedLocked();
		}
	}

	private static void EnsureLatchLoadedLocked()
	{
		if (_latchLoaded || !SystemAudioRuntime.IsConfigured)
		{
			return;
		}
		try
		{
			string latchPath = LatchPath;
			if (File.Exists(latchPath))
			{
				LatchFile latchFile = JsonSerializer.Deserialize<LatchFile>(File.ReadAllText(latchPath));
				if ((object)latchFile != null && string.Equals(latchFile.AppVersion, Settings.AppVersion, StringComparison.Ordinal) && string.Equals(latchFile.OsVersion, Settings.OsVersion, StringComparison.Ordinal))
				{
					_failures = Math.Max(0, latchFile.Failures);
					_lastReason = latchFile.LastReason;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Capture("system audio latch load failed: " + ex.Message, "EnsureLatchLoadedLocked", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioPolicy.cs", 313);
		}
		finally
		{
			_latchLoaded = true;
		}
	}

	private static string SafeAppVersion()
	{
		try
		{
			return Settings.AppVersion;
		}
		catch
		{
			return "";
		}
	}

	private static string SafeOsVersion()
	{
		try
		{
			return Settings.OsVersion;
		}
		catch
		{
			return "";
		}
	}

	internal static void ResetForTest()
	{
		lock (LatchSync)
		{
			_served = -1;
			_lastAppliedServed = -1;
			_lastAppliedEnv = -1;
			_env = -1;
			_unparseableLogged = 0;
			_failures = 0;
			_lastReason = null;
			_latchLoaded = false;
			LatchPathOverride = null;
		}
	}
}
