using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class UploadManifest
{
	[JsonPropertyName("schema")]
	public int Schema { get; set; } = 1;

	[JsonPropertyName("state")]
	public UploadManifestState State { get; set; } = UploadManifestState.Ready;

	[JsonPropertyName("session_id")]
	public string SessionId { get; set; } = "";

	[JsonPropertyName("segment_index")]
	public int SegmentIndex { get; set; }

	[JsonPropertyName("attempts")]
	public int Attempts { get; set; }

	[JsonPropertyName("next_retry_wall_ms")]
	public long? NextRetryWallMs { get; set; }

	[JsonPropertyName("last_attempt_wall_ms")]
	public long? LastAttemptWallMs { get; set; }

	[JsonPropertyName("urls_issued_wall_ms")]
	public long? UrlsIssuedWallMs { get; set; }

	[JsonPropertyName("last_error_code")]
	public string? LastErrorCode { get; set; }

	[JsonPropertyName("last_error_message")]
	public string? LastErrorMessage { get; set; }

	[JsonPropertyName("updated_wall_ms")]
	public long UpdatedWallMs { get; set; }

	[JsonPropertyName("files")]
	public List<UploadFileManifest> Files { get; set; } = new List<UploadFileManifest>();
}
