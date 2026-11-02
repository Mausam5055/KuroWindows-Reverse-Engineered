using System;
using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopCaptureRecoveryFlags
{
	[JsonPropertyName("version")]
	public string? Version { get; init; }

	[JsonPropertyName("denied_hold_seconds")]
	public int? DeniedHoldSeconds { get; init; }

	[JsonPropertyName("remote_policy")]
	public string? RemotePolicy { get; init; }

	[JsonIgnore]
	public bool LegacyCadence => string.Equals(Version?.Trim(), "v1", StringComparison.OrdinalIgnoreCase);

	[JsonIgnore]
	public string EffectiveRemotePolicy
	{
		get
		{
			string text = RemotePolicy?.Trim().ToLowerInvariant();
			if (!(text == "hold_only"))
			{
				if (text == "off")
				{
					return "off";
				}
				return "full";
			}
			return "hold_only";
		}
	}

	public static readonly DesktopCaptureRecoveryFlags Defaults = new DesktopCaptureRecoveryFlags();
}
