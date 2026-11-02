using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopFeatureFlags
{
	[JsonPropertyName("text_selection_enabled")]
	public bool TextSelectionEnabled { get; init; } = true;

	[JsonPropertyName("uia_secure_field_enabled")]
	public bool UiaSecureFieldEnabled { get; init; } = true;

	[JsonPropertyName("uia_blocked_window_classes")]
	public string[] UiaBlockedWindowClasses { get; init; } = Array.Empty<string>();

	[JsonPropertyName("uia_blocked_apps")]
	public string[] UiaBlockedApps { get; init; } = Array.Empty<string>();

	[JsonPropertyName("uia_probe_enabled")]
	public bool UiaProbeEnabled { get; init; } = true;

	[JsonPropertyName("wire_profile")]
	public string? WireProfile { get; init; }

	[JsonPropertyName("encoder_watchdog")]
	public bool EncoderWatchdog { get; init; } = true;

	[JsonPropertyName("capture_recovery")]
	public DesktopCaptureRecoveryFlags? CaptureRecovery { get; init; }

	[JsonPropertyName("pad_policy")]
	public string? PadPolicy { get; init; }

	[JsonIgnore]
	public bool LegacyPadCadence => string.Equals(PadPolicy?.Trim(), "v1", StringComparison.OrdinalIgnoreCase);

	[JsonPropertyName("system_audio")]
	public bool SystemAudio { get; init; }

	public static readonly DesktopFeatureFlags Defaults = new DesktopFeatureFlags();

	public static volatile DesktopFeatureFlags Seeded = new DesktopFeatureFlags();

	public DesktopFeatureFlags Normalize()
	{
		return this with
		{
			UiaBlockedWindowClasses = CleanArray(UiaBlockedWindowClasses),
			UiaBlockedApps = CleanArray(UiaBlockedApps)
		};
	}

	private static string[] CleanArray(string[]? values)
	{
		return values?.Select((string v) => v.Trim()).Where((string v) => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray() ?? Array.Empty<string>();
	}
}
