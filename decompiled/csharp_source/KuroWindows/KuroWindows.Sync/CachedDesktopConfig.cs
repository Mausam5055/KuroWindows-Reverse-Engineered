using System;
using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record CachedDesktopConfig
{
	[JsonPropertyName("schema_version")]
	public int SchemaVersion { get; init; }

	[JsonPropertyName("config_version")]
	public long ConfigVersion { get; init; }

	[JsonPropertyName("cached_at")]
	public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

	[JsonPropertyName("config")]
	public DesktopConfig Config { get; init; } = new DesktopConfig();

	public const int CurrentSchemaVersion = 7;

	public bool TryValidate(out string error)
	{
		if (SchemaVersion != 7)
		{
			error = $"schema_version must be {7}";
			return false;
		}
		if (ConfigVersion < 0)
		{
			error = "config_version cannot be negative";
			return false;
		}
		if (!HasCurrentVideoPolicy(Config.Video))
		{
			error = "config.video with bits_per_pixel_per_second, min_mbps, and max_mbps is required";
			return false;
		}
		return Config.TryValidate(out error);
	}

	private static bool HasCurrentVideoPolicy(DesktopVideoPolicy? policy)
	{
		double? num = policy?.BitsPerPixelPerSecond;
		if (num.HasValue && num.GetValueOrDefault() > 0.0)
		{
			num = policy.MinMbps;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				num = policy.MaxMbps;
				if (num.HasValue && num.GetValueOrDefault() > 0.0)
				{
					return policy.MinMbps <= policy.MaxMbps;
				}
			}
		}
		return false;
	}
}
