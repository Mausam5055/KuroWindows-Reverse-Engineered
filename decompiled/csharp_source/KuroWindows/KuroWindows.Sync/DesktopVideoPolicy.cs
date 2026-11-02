using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopVideoPolicy
{
	[JsonPropertyName("bits_per_pixel_per_second")]
	public double? BitsPerPixelPerSecond { get; init; }

	[JsonPropertyName("min_mbps")]
	public double? MinMbps { get; init; }

	[JsonPropertyName("max_mbps")]
	public double? MaxMbps { get; init; }
}
