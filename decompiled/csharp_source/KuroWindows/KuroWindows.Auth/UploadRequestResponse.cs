using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record UploadRequestResponse
{
	[JsonPropertyName("recording_session_id")]
	public string? RecordingSessionId { get; init; }

	[JsonPropertyName("recording_segment_id")]
	public string? RecordingSegmentId { get; init; }

	[JsonPropertyName("status")]
	public string Status { get; init; } = "pending";

	[JsonPropertyName("platform")]
	public string? Platform { get; init; }

	[JsonPropertyName("s3_prefix")]
	public string? S3Prefix { get; init; }

	[JsonPropertyName("expires_in")]
	public int? ExpiresIn { get; init; }

	[JsonPropertyName("upload_urls")]
	public Dictionary<string, UploadUrlInfo> UploadUrls { get; init; } = new Dictionary<string, UploadUrlInfo>();

	[JsonPropertyName("file_hashes")]
	public Dictionary<string, string>? FileHashes { get; init; }
}
