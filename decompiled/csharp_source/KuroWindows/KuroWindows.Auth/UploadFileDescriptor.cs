using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record UploadFileDescriptor
{
	[JsonPropertyName("name")]
	public string Name { get; init; } = "";

	[JsonPropertyName("content_type")]
	public string ContentType { get; init; } = "";

	[JsonPropertyName("size_bytes")]
	public long SizeBytes { get; init; }

	[JsonPropertyName("sha256")]
	public string? Sha256 { get; init; }

	[JsonPropertyName("part_sha256")]
	public IReadOnlyList<string>? PartSha256 { get; init; }
}
