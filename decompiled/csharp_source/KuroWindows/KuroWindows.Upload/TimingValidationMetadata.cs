using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class TimingValidationMetadata
{
	[JsonPropertyName("suspect")]
	public bool Suspect { get; set; }

	[JsonPropertyName("suspect_reason")]
	public string? SuspectReason { get; set; }

	[JsonPropertyName("sidecar_integrity_error")]
	public string? SidecarIntegrityError { get; set; }

	[JsonPropertyName("encoded_frame_count")]
	public long? EncodedFrameCount { get; set; }

	[JsonPropertyName("capture_fps")]
	public int? CaptureFps { get; set; }

	[JsonPropertyName("bitrate_bps")]
	public uint? BitrateBps { get; set; }

	[JsonPropertyName("fallback_mode")]
	public string? FallbackMode { get; set; }
}
