using System;
using System.Globalization;
using System.Threading;
using KuroWindows.Sync;

namespace KuroWindows.Capture;

public static class VideoBitratePolicy
{
	private sealed record PolicyState(DesktopVideoPolicy Video, string Source);

	public const string EnvBppsName = "KURO_VIDEO_BPPS";

	public const string EnvMinMbpsName = "KURO_VIDEO_MIN_MBPS";

	public const string EnvMaxMbpsName = "KURO_VIDEO_MAX_MBPS";

	public const string SourceEnv = "env";

	public const string SourceAppOverride = "app_override";

	public const string SourceGlobalDefault = "global_default";

	public const string SourceFallback = "fallback";

	private const double BppsMin = 0.5;

	private const double BppsMax = 10.0;

	private const double MbpsFloor = 4.0;

	private const double MbpsCeiling = 30.0;

	private const double EnvBaseBpps = 10.0;

	private const double EnvBaseMinMbps = 10.0;

	private const double EnvBaseMaxMbps = 24.0;

	private static volatile PolicyState? _policy;

	private static volatile string _lastAppliedDescription = "absent";

	private static int _invalidPolicyLogged;

	private static int _invalidEnvLogged;

	internal static Func<string, string?> EnvironmentReader { get; set; } = Environment.GetEnvironmentVariable;

	public static void Apply(DesktopVideoPolicy? video, string? source)
	{
		PolicyState policyState = null;
		if ((object)video != null)
		{
			if (TrySanitize(video, out DesktopVideoPolicy clean))
			{
				policyState = new PolicyState(clean, NormalizeSource(source));
				Interlocked.Exchange(ref _invalidPolicyLogged, 0);
			}
			else if (Interlocked.Exchange(ref _invalidPolicyLogged, 1) == 0)
			{
				KuroLog.Capture("video bitrate policy invalid; using compiled-in fallback bpps=" + (video.BitsPerPixelPerSecond?.ToString() ?? "-") + " min=" + (video.MinMbps?.ToString() ?? "-") + " max=" + (video.MaxMbps?.ToString() ?? "-"), "Apply", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoBitratePolicy.cs", 86);
			}
		}
		_policy = policyState;
		string text = (((object)policyState == null) ? "absent" : $"bpps={policyState.Video.BitsPerPixelPerSecond} min={policyState.Video.MinMbps} max={policyState.Video.MaxMbps} source={policyState.Source}");
		if (!string.Equals(_lastAppliedDescription, text, StringComparison.Ordinal))
		{
			_lastAppliedDescription = text;
			KuroLog.Capture("video bitrate policy applied " + text, "Apply", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoBitratePolicy.cs", 100);
		}
	}

	public static void Clear(string reason)
	{
		if ((object)_policy != null)
		{
			_policy = null;
			_lastAppliedDescription = "absent";
			KuroLog.Capture("video bitrate policy cleared reason=" + reason, "Clear", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoBitratePolicy.cs", 114);
		}
	}

	public static VideoBitrateSnapshot? Snapshot()
	{
		PolicyState policy = _policy;
		double? num = ReadEnvDouble("KURO_VIDEO_BPPS");
		double? num2 = ReadEnvDouble("KURO_VIDEO_MIN_MBPS");
		double? num3 = ReadEnvDouble("KURO_VIDEO_MAX_MBPS");
		bool flag = num.HasValue || num2.HasValue || num3.HasValue;
		if ((object)policy == null && !flag)
		{
			return null;
		}
		double num4 = policy?.Video.BitsPerPixelPerSecond ?? 10.0;
		double num5 = policy?.Video.MinMbps ?? 10.0;
		double num6 = policy?.Video.MaxMbps ?? 24.0;
		double bitsPerPixelPerSecond = Math.Clamp(num ?? num4, 0.5, 10.0);
		double num7 = Math.Clamp(num2 ?? num5, 4.0, 30.0);
		double num8 = Math.Clamp(num3 ?? num6, 4.0, 30.0);
		if (num7 > num8)
		{
			num7 = num8;
		}
		string source = (flag ? "env" : policy.Source);
		return new VideoBitrateSnapshot(bitsPerPixelPerSecond, num7, num8, source);
	}

	private static bool TrySanitize(DesktopVideoPolicy video, out DesktopVideoPolicy clean)
	{
		clean = video;
		double? bitsPerPixelPerSecond = video.BitsPerPixelPerSecond;
		if (bitsPerPixelPerSecond.HasValue)
		{
			double valueOrDefault = bitsPerPixelPerSecond.GetValueOrDefault();
			if (double.IsFinite(valueOrDefault))
			{
				bitsPerPixelPerSecond = video.MinMbps;
				if (bitsPerPixelPerSecond.HasValue)
				{
					double valueOrDefault2 = bitsPerPixelPerSecond.GetValueOrDefault();
					if (double.IsFinite(valueOrDefault2))
					{
						bitsPerPixelPerSecond = video.MaxMbps;
						if (bitsPerPixelPerSecond.HasValue)
						{
							double valueOrDefault3 = bitsPerPixelPerSecond.GetValueOrDefault();
							if (double.IsFinite(valueOrDefault3))
							{
								valueOrDefault = Math.Clamp(valueOrDefault, 0.5, 10.0);
								valueOrDefault2 = Math.Clamp(valueOrDefault2, 4.0, 30.0);
								valueOrDefault3 = Math.Clamp(valueOrDefault3, 4.0, 30.0);
								if (valueOrDefault2 > valueOrDefault3)
								{
									return false;
								}
								clean = new DesktopVideoPolicy
								{
									BitsPerPixelPerSecond = valueOrDefault,
									MinMbps = valueOrDefault2,
									MaxMbps = valueOrDefault3
								};
								return true;
							}
						}
					}
				}
			}
		}
		return false;
	}

	private static string NormalizeSource(string? source)
	{
		if (!(source == "app_override"))
		{
			if (source == "global_default")
			{
				return "global_default";
			}
			return "global_default";
		}
		return "app_override";
	}

	private static double? ReadEnvDouble(string name)
	{
		string text = EnvironmentReader(name);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result))
		{
			return result;
		}
		if (Interlocked.Exchange(ref _invalidEnvLogged, 1) == 0)
		{
			KuroLog.Capture("video bitrate env seam unparseable; ignoring " + name + "=" + text, "ReadEnvDouble", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoBitratePolicy.cs", 205);
		}
		return null;
	}

	internal static void ResetForTest()
	{
		_policy = null;
		_lastAppliedDescription = "absent";
		_invalidPolicyLogged = 0;
		_invalidEnvLogged = 0;
		EnvironmentReader = Environment.GetEnvironmentVariable;
	}
}
