using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class UploadPartManifest
{
	[JsonPropertyName("part_number")]
	public int PartNumber { get; set; }

	[JsonPropertyName("offset")]
	public long Offset { get; set; }

	[JsonPropertyName("size_bytes")]
	public long SizeBytes { get; set; }

	[JsonPropertyName("etag")]
	public string? ETag { get; set; }

	[JsonPropertyName("uploaded_wall_ms")]
	public long? UploadedWallMs { get; set; }
}
