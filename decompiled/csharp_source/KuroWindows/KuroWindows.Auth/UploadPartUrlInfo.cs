using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record UploadPartUrlInfo
{
	[JsonPropertyName("part_number")]
	public int PartNumber { get; init; }

	[JsonPropertyName("offset")]
	public long Offset { get; init; }

	[JsonPropertyName("size_bytes")]
	public long SizeBytes { get; init; }

	[JsonPropertyName("url")]
	public string Url { get; init; } = "";

	[JsonPropertyName("headers")]
	public Dictionary<string, string>? Headers { get; init; }
}
