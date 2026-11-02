using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SegmentMetadata
{
	[JsonPropertyName("segment_index")]
	public int SegmentIndex { get; set; }

	[JsonPropertyName("display_index")]
	public int DisplayIndex { get; set; }

	[JsonPropertyName("claimed_duration_ms")]
	public long ClaimedDurationMs { get; set; }

	[JsonPropertyName("idle_duration_ms")]
	public long IdleDurationMs { get; set; }

	[JsonPropertyName("duration_seconds")]
	public double DurationSeconds { get; set; }
}
