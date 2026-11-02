using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record MultipartCompleteRequest
{
	[JsonPropertyName("client_session_id")]
	public string ClientSessionId { get; init; } = "";

	[JsonPropertyName("platform")]
	public string Platform { get; init; } = "windows";

	[JsonPropertyName("device_id")]
	public string DeviceId { get; init; } = "";

	[JsonPropertyName("segment_index")]
	public int SegmentIndex { get; init; }

	[JsonPropertyName("file_name")]
	public string FileName { get; init; } = "";

	[JsonPropertyName("upload_id")]
	public string UploadId { get; init; } = "";

	[JsonPropertyName("expected_size_bytes")]
	public long ExpectedSizeBytes { get; init; }

	[JsonPropertyName("parts")]
	public List<MultipartPartComplete> Parts { get; init; } = new List<MultipartPartComplete>();
}
