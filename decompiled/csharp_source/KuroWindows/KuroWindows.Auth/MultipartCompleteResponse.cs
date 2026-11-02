using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record MultipartCompleteResponse
{
	[JsonPropertyName("status")]
	public string Status { get; init; } = "";

	[JsonPropertyName("key")]
	public string Key { get; init; } = "";

	[JsonPropertyName("size_bytes")]
	public long SizeBytes { get; init; }

	[JsonPropertyName("etag")]
	public string? ETag { get; init; }
}
