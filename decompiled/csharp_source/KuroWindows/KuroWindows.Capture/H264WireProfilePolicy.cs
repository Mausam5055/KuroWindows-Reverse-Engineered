using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using KuroWindows.Sync;

namespace KuroWindows.Capture;

public static class H264WireProfilePolicy
{
	private sealed record ProbeVerdictFile(string? Verdict, string? AppVersion, string? OsVersion, DateTimeOffset RecordedAt);

	public const string EnvName = "KURO_WIRE_PROFILE";

	public const string High = "high";

	public const string Legacy = "legacy";

	public const string SourceStallLatch = "stall_latch";

	public const string SourceEnv = "env";

	public const string SourceServer = "server";

	public const string SourceDefault = "default";

	private static volatile string? _serverValue;

	private static volatile string _lastAppliedDescription = "absent";

	private static int _latched;

	private static int _unrecognizedLogged;

	private const string ProbeVerdictWorks = "works";

	private const string ProbeVerdictChannelsDead = "channels_dead";

	private static readonly object ProbeSync = new object();

	private static volatile bool _probeLoaded;

	private static volatile string? _processProbeVerdict;

	internal static Func<string, string?> EnvironmentReader { get; set; } = Environment.GetEnvironmentVariable;

	public static bool IsLatched => Volatile.Read(in _latched) != 0;

	internal static string? ProbeCachePathOverride { get; set; }

	private static string ProbeCachePath => ProbeCachePathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "wire_profile_probe.json");

	private static string ProbeEnvironmentKeyAppVersion => AppConfig.AppVersion;

	private static string ProbeEnvironmentKeyOsVersion => Environment.OSVersion.Version.ToString();

	public static bool HighAttemptWorthwhile
	{
		get
		{
			EnsureProbeVerdictLoaded();
			return !string.Equals(_processProbeVerdict, "channels_dead", StringComparison.Ordinal);
		}
	}

	public static void Apply(DesktopFeatureFlags? flags)
	{
		string text = (_serverValue = Normalize(flags?.WireProfile, logUnrecognized: true)) ?? "absent";
		if (!string.Equals(_lastAppliedDescription, text, StringComparison.Ordinal))
		{
			_lastAppliedDescription = text;
			KuroLog.Capture("wire profile policy applied value=" + text, "Apply", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 72);
		}
	}

	public static void Clear(string reason)
	{
		if (_serverValue != null)
		{
			_serverValue = null;
			_lastAppliedDescription = "absent";
			KuroLog.Capture("wire profile policy cleared reason=" + reason, "Clear", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 86);
		}
	}

	public static void LatchLegacy(string reason)
	{
		if (Interlocked.Exchange(ref _latched, 1) == 0)
		{
			KuroLog.Capture("encoder wire profile latched legacy reason=" + reason, "LatchLegacy", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 101);
		}
	}

	public static (string Profile, string Source) SnapshotForOpen()
	{
		if (IsLatched)
		{
			return (Profile: "legacy", Source: "stall_latch");
		}
		string text = Normalize(EnvironmentReader("KURO_WIRE_PROFILE"), logUnrecognized: false);
		if (text != null)
		{
			return (Profile: text, Source: "env");
		}
		string serverValue = _serverValue;
		if (serverValue == null)
		{
			return (Profile: "high", Source: "default");
		}
		return (Profile: serverValue, Source: "server");
	}

	private static string? Normalize(string? raw, bool logUnrecognized)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}
		string text = raw.Trim().ToLowerInvariant();
		if ((text == "high" || text == "legacy") ? true : false)
		{
			Interlocked.Exchange(ref _unrecognizedLogged, 0);
			return text;
		}
		if (logUnrecognized && Interlocked.Exchange(ref _unrecognizedLogged, 1) == 0)
		{
			KuroLog.Capture("wire profile policy unrecognized value=" + raw.Trim() + "; using compiled default", "Normalize", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 148);
		}
		return null;
	}

	public static void RecordHighOpenOutcome(bool succeeded)
	{
		string text = (succeeded ? "works" : "channels_dead");
		if (string.Equals(_processProbeVerdict, text, StringComparison.Ordinal))
		{
			return;
		}
		_processProbeVerdict = text;
		KuroLog.Capture($"wire profile probe verdict={text} app={ProbeEnvironmentKeyAppVersion} os={ProbeEnvironmentKeyOsVersion}", "RecordHighOpenOutcome", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 239);
		lock (ProbeSync)
		{
			try
			{
				string probeCachePath = ProbeCachePath;
				Directory.CreateDirectory(Path.GetDirectoryName(probeCachePath));
				File.WriteAllText(probeCachePath, JsonSerializer.Serialize(new ProbeVerdictFile(text, ProbeEnvironmentKeyAppVersion, ProbeEnvironmentKeyOsVersion, DateTimeOffset.UtcNow)));
			}
			catch (Exception ex)
			{
				KuroLog.Capture("wire profile probe persist failed: " + ex.Message, "RecordHighOpenOutcome", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 256);
			}
		}
	}

	private static void EnsureProbeVerdictLoaded()
	{
		if (_probeLoaded)
		{
			return;
		}
		lock (ProbeSync)
		{
			if (_probeLoaded)
			{
				return;
			}
			try
			{
				string probeCachePath = ProbeCachePath;
				if (!File.Exists(probeCachePath))
				{
					return;
				}
				ProbeVerdictFile probeVerdictFile = JsonSerializer.Deserialize<ProbeVerdictFile>(File.ReadAllText(probeCachePath));
				bool flag = (object)probeVerdictFile != null && string.Equals(probeVerdictFile.AppVersion, ProbeEnvironmentKeyAppVersion, StringComparison.Ordinal) && string.Equals(probeVerdictFile.OsVersion, ProbeEnvironmentKeyOsVersion, StringComparison.Ordinal);
				if (flag)
				{
					string verdict = probeVerdictFile.Verdict;
					bool flag2 = ((verdict == "works" || verdict == "channels_dead") ? true : false);
					flag = flag2;
				}
				if (flag)
				{
					_processProbeVerdict = probeVerdictFile.Verdict;
					if (probeVerdictFile.Verdict == "channels_dead")
					{
						KuroLog.Capture($"wire profile probe verdict loaded verdict={probeVerdictFile.Verdict} app={probeVerdictFile.AppVersion} os={probeVerdictFile.OsVersion}; opening legacy without high attempts", "EnsureProbeVerdictLoaded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 293);
					}
				}
			}
			catch (Exception ex)
			{
				KuroLog.Capture("wire profile probe load failed: " + ex.Message, "EnsureProbeVerdictLoaded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\H264WireProfilePolicy.cs", 302);
			}
			finally
			{
				_probeLoaded = true;
			}
		}
	}

	internal static void ResetForTest()
	{
		_serverValue = null;
		_lastAppliedDescription = "absent";
		_latched = 0;
		_unrecognizedLogged = 0;
		_probeLoaded = false;
		_processProbeVerdict = null;
		ProbeCachePathOverride = null;
		EnvironmentReader = Environment.GetEnvironmentVariable;
	}
}
