using System;

namespace KuroWindows.RecorderCore;

public static class RecorderCorePolicy
{
	private sealed record PolicyState(RecorderCoreKind Kind, string Source);

	public const string EnvVarName = "KURO_RECORDER_CORE";

	public const string CpuV1Name = "cpu_v1";

	public const string GpuV2Name = "gpu_v2";

	public const string SourceEnv = "env";

	public const string SourceUserOverride = "user_override";

	public const string SourceGlobalDefault = "global_default";

	public const string SourceFallback = "fallback";

	private static volatile PolicyState? _policy;

	internal static Func<string, string?> EnvironmentReader { get; set; } = Environment.GetEnvironmentVariable;

	public static void ApplyPolicy(string? recorderCore, string? source)
	{
		string text = recorderCore?.Trim();
		if (string.Equals(text, "cpu_v1", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "gpu_v2", StringComparison.OrdinalIgnoreCase))
		{
			_policy = new PolicyState(Parse(text), NormalizeSource(source));
		}
		else
		{
			_policy = null;
		}
	}

	public static void ClearPolicy()
	{
		_policy = null;
	}

	public static RecorderCoreResolution Resolve()
	{
		string text = EnvironmentReader("KURO_RECORDER_CORE");
		if (!string.IsNullOrWhiteSpace(text))
		{
			return new RecorderCoreResolution(Parse(text), "env");
		}
		PolicyState policy = _policy;
		if ((object)policy != null)
		{
			return new RecorderCoreResolution(policy.Kind, policy.Source);
		}
		return new RecorderCoreResolution(RecorderCoreKind.CpuV1, "fallback");
	}

	public static RecorderCoreKind Requested()
	{
		return Resolve().Kind;
	}

	public static RecorderCoreKind Parse(string? raw)
	{
		if (!string.Equals(raw?.Trim(), "gpu_v2", StringComparison.OrdinalIgnoreCase))
		{
			return RecorderCoreKind.CpuV1;
		}
		return RecorderCoreKind.GpuV2;
	}

	public static string Name(RecorderCoreKind kind)
	{
		if (kind != RecorderCoreKind.GpuV2)
		{
			return "cpu_v1";
		}
		return "gpu_v2";
	}

	private static string NormalizeSource(string? source)
	{
		if (!(source == "user_override"))
		{
			if (source == "global_default")
			{
				return "global_default";
			}
			return "global_default";
		}
		return "user_override";
	}

	internal static void ResetForTest()
	{
		_policy = null;
		EnvironmentReader = Environment.GetEnvironmentVariable;
	}
}
