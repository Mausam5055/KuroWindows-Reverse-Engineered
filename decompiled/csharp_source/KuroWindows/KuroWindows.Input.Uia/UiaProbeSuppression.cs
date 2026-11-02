using System;
using System.Collections.Concurrent;

namespace KuroWindows.Input.Uia;

internal static class UiaProbeSuppression
{
	private static readonly ConcurrentDictionary<string, long> SuppressedUntilMs = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

	internal static bool IsSuppressed(string? exeName)
	{
		if (!string.IsNullOrEmpty(exeName) && SuppressedUntilMs.TryGetValue(exeName, out var value))
		{
			return Clock() < value;
		}
		return false;
	}

	internal static bool Suppress(string? exeName, long durationMs)
	{
		if (string.IsNullOrEmpty(exeName))
		{
			return false;
		}
		long num = Clock();
		bool num2 = SuppressedUntilMs.TryGetValue(exeName, out var value) && num < value;
		SuppressedUntilMs[exeName] = num + durationMs;
		return !num2;
	}

	internal static void ResetForTests()
	{
		SuppressedUntilMs.Clear();
		Clock = () => Environment.TickCount64;
	}
}
