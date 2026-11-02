using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record UploadUrlInfo
{
	[JsonPropertyName("url")]
	public string Url { get; init; } = "";

	[JsonPropertyName("headers")]
	public Dictionary<string, string>? Headers { get; init; }

	[JsonPropertyName("content_type")]
	public string? ContentType { get; init; }

	[JsonPropertyName("mode")]
	public string Mode { get; init; } = "single_put";

	[JsonPropertyName("key")]
	public string? Key { get; init; }

	[JsonPropertyName("upload_id")]
	public string? UploadId { get; init; }

	[JsonPropertyName("part_size_bytes")]
	public long? PartSizeBytes { get; init; }

	[JsonPropertyName("size_bytes")]
	public long? SizeBytes { get; init; }

	[JsonPropertyName("sha256")]
	public string? Sha256 { get; init; }

	[JsonPropertyName("parts")]
	public List<UploadPartUrlInfo>? Parts { get; init; }
}
