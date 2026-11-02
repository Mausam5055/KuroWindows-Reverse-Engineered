using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record UploadCompleteResponse
{
	[JsonPropertyName("recording_session_id")]
	public string? RecordingSessionId { get; init; }

	[JsonPropertyName("recording_segment_id")]
	public string? RecordingSegmentId { get; init; }

	[JsonPropertyName("status")]
	public string Status { get; init; } = "";

	[JsonPropertyName("uploaded_duration_ms")]
	public long UploadedDurationMs { get; init; }

	[JsonPropertyName("credited_amount_microusd")]
	public long CreditedAmountMicrousd { get; init; }
}
