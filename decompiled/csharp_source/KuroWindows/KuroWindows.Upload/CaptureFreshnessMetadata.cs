using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class CaptureFreshnessMetadata
{
	[JsonPropertyName("suspect")]
	public bool Suspect { get; set; }

	[JsonPropertyName("suspect_reason")]
	public string? SuspectReason { get; set; }

	[JsonPropertyName("encoded_frame_count")]
	public long? EncodedFrameCount { get; set; }

	[JsonPropertyName("source_frame_count")]
	public long? SourceFrameCount { get; set; }

	[JsonPropertyName("cursor_only_frame_count")]
	public long? CursorOnlyFrameCount { get; set; }

	[JsonPropertyName("duplicate_frame_count")]
	public long? DuplicateFrameCount { get; set; }

	[JsonPropertyName("source_frame_duration_ms")]
	public long? SourceFrameDurationMs { get; set; }

	[JsonPropertyName("cursor_only_frame_duration_ms")]
	public long? CursorOnlyFrameDurationMs { get; set; }

	[JsonPropertyName("duplicate_frame_duration_ms")]
	public long? DuplicateFrameDurationMs { get; set; }

	[JsonPropertyName("active_input_event_count")]
	public long? ActiveInputEventCount { get; set; }

	[JsonPropertyName("unique_source_frame_hash_count")]
	public long? UniqueSourceFrameHashCount { get; set; }

	[JsonPropertyName("source_frame_visual_change_score")]
	public long? SourceFrameVisualChangeScore { get; set; }

	[JsonPropertyName("capture_fps")]
	public int? CaptureFps { get; set; }

	[JsonPropertyName("bitrate_bps")]
	public uint? BitrateBps { get; set; }

	[JsonPropertyName("fallback_mode")]
	public string? FallbackMode { get; set; }
}
