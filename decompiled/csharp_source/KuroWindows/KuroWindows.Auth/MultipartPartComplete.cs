using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record MultipartPartComplete
{
	[JsonPropertyName("part_number")]
	public int PartNumber { get; init; }

	[JsonPropertyName("etag")]
	public string ETag { get; init; } = "";

	[JsonPropertyName("sha256")]
	public string? Sha256 { get; init; }
}
