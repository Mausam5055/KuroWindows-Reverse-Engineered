using System;
using KuroWindows.Sync;

namespace KuroWindows.Upload;

public static class UploadRetentionPolicy
{
	private sealed record PolicyState(TimeSpan Age);

	public const double FallbackHours = 168.0;

	public const double MinHours = 1.0;

	public const double MaxHours = 168.0;

	private static readonly TimeSpan Fallback = TimeSpan.FromHours(168.0);

	private static volatile PolicyState? _policy;

	private static volatile string _lastAppliedDescription = "absent";

	public static TimeSpan BacklogPurgeAge => _policy?.Age ?? Fallback;

	public static void Apply(DesktopUploadPolicy? upload)
	{
		PolicyState policyState = null;
		double? num = upload?.BacklogPurgeHours;
		if (num.HasValue)
		{
			double valueOrDefault = num.GetValueOrDefault();
			if (double.IsFinite(valueOrDefault))
			{
				policyState = new PolicyState(TimeSpan.FromHours(Math.Clamp(valueOrDefault, 1.0, 168.0)));
			}
		}
		_policy = policyState;
		string text = (((object)policyState == null) ? "absent" : $"hours={policyState.Age.TotalHours:F0}");
		if (!string.Equals(_lastAppliedDescription, text, StringComparison.Ordinal))
		{
			_lastAppliedDescription = text;
			KuroLog.Upload($"backlog purge policy applied {text} (fallback {168.0:F0}h, clamp [{1.0:F0},{168.0:F0}]h)", "Apply", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadRetentionPolicy.cs", 55);
		}
	}

	internal static void ResetForTest()
	{
		_policy = null;
		_lastAppliedDescription = "absent";
	}
}
