using System;
using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionMetadata
{
	[JsonPropertyName("client_session_id")]
	public string ClientSessionId { get; set; } = "";

	[JsonPropertyName("captured_at")]
	public DateTimeOffset? CapturedAt { get; set; }

	[JsonPropertyName("uploaded_at")]
	public DateTimeOffset UploadedAt { get; set; }
}
