using System.Text.Json.Serialization;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentDisplayManifest
{
	[JsonPropertyName("display_index")]
	public int DisplayIndex { get; set; }

	[JsonPropertyName("original_screen_index")]
	public int? OriginalScreenIndex { get; set; }

	[JsonPropertyName("display_role")]
	public string? DisplayRole { get; set; }

	[JsonPropertyName("backend")]
	public CaptureBackendKind Backend { get; set; }

	[JsonPropertyName("video_file")]
	public string VideoFile { get; set; } = "";

	[JsonPropertyName("events_file")]
	public string EventsFile { get; set; } = "";

	[JsonPropertyName("frame_sidecar_file")]
	public string? FrameSidecarFile { get; set; }

	[JsonPropertyName("capture_fps")]
	public int CaptureFps { get; set; }

	[JsonPropertyName("native_width")]
	public int? NativeWidth { get; set; }

	[JsonPropertyName("native_height")]
	public int? NativeHeight { get; set; }

	[JsonPropertyName("encoded_width")]
	public int? EncodedWidth { get; set; }

	[JsonPropertyName("encoded_height")]
	public int? EncodedHeight { get; set; }

	[JsonPropertyName("resolution_mode")]
	public string? ResolutionMode { get; set; }

	[JsonPropertyName("resolution_throttled")]
	public bool? ResolutionThrottled { get; set; }

	[JsonPropertyName("bitrate_bps")]
	public uint BitrateBps { get; set; }

	[JsonPropertyName("encoder_backend")]
	public string? EncoderBackend { get; set; }

	[JsonPropertyName("input_memory_path")]
	public string? InputMemoryPath { get; set; }

	[JsonPropertyName("hardware_transforms_requested")]
	public bool? HardwareTransformsRequested { get; set; }

	[JsonPropertyName("encoder_selection_reason")]
	public string? EncoderSelectionReason { get; set; }

	[JsonPropertyName("fallback_mode")]
	public string? FallbackMode { get; set; }

	[JsonPropertyName("first_frame_wall_ms")]
	public long? FirstFrameWallMs { get; set; }

	[JsonPropertyName("last_frame_wall_ms")]
	public long? LastFrameWallMs { get; set; }

	[JsonPropertyName("first_pts_ns")]
	public long? FirstPtsNs { get; set; }

	[JsonPropertyName("last_pts_ns")]
	public long? LastPtsNs { get; set; }

	[JsonPropertyName("last_frame_duration_ns")]
	public long? LastFrameDurationNs { get; set; }

	[JsonPropertyName("encoded_frame_count")]
	public long EncodedFrameCount { get; set; }

	[JsonPropertyName("timestamp_quality")]
	public string? TimestampQuality { get; set; }

	[JsonPropertyName("source_frame_count")]
	public long SourceFrameCount { get; set; }

	[JsonPropertyName("cursor_only_frame_count")]
	public long CursorOnlyFrameCount { get; set; }

	[JsonPropertyName("duplicate_frame_count")]
	public long DuplicateFrameCount { get; set; }

	[JsonPropertyName("source_frame_duration_ns")]
	public long SourceFrameDurationNs { get; set; }

	[JsonPropertyName("cursor_only_frame_duration_ns")]
	public long CursorOnlyFrameDurationNs { get; set; }

	[JsonPropertyName("duplicate_frame_duration_ns")]
	public long DuplicateFrameDurationNs { get; set; }

	[JsonPropertyName("degraded_frame_count")]
	public long DegradedFrameCount { get; set; }

	[JsonPropertyName("active_input_event_count")]
	public long ActiveInputEventCount { get; set; }

	[JsonPropertyName("unique_source_frame_hash_count")]
	public long UniqueSourceFrameHashCount { get; set; }

	[JsonPropertyName("source_frame_visual_change_score")]
	public long SourceFrameVisualChangeScore { get; set; }

	[JsonPropertyName("sidecar_integrity_error")]
	public string? SidecarIntegrityError { get; set; }

	[JsonPropertyName("timing_suspect")]
	public bool TimingSuspect { get; set; }

	[JsonPropertyName("timing_suspect_reason")]
	public string? TimingSuspectReason { get; set; }

	[JsonPropertyName("capture_surface_stale")]
	public bool CaptureSurfaceStale { get; set; }

	[JsonPropertyName("capture_surface_stale_reason")]
	public string? CaptureSurfaceStaleReason { get; set; }

	[JsonPropertyName("audio_first_frame_offset_us")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? AudioFirstFrameOffsetUs { get; set; }

	[JsonIgnore]
	public long? FabricatedFrameCount { get; set; }

	[JsonIgnore]
	public long? FabricatedFrameDurationNs { get; set; }

	[JsonIgnore]
	public long? NonFabricatedFrameCount { get; set; }

	[JsonIgnore]
	public long? NonFabricatedFrameDurationNs { get; set; }
}
