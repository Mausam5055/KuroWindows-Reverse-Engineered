using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class UploadFileManifest
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("size_bytes")]
	public long SizeBytes { get; set; }

	[JsonPropertyName("uploaded")]
	public bool Uploaded { get; set; }

	[JsonPropertyName("uploaded_wall_ms")]
	public long? UploadedWallMs { get; set; }

	[JsonPropertyName("last_error_code")]
	public string? LastErrorCode { get; set; }

	[JsonPropertyName("mode")]
	public string Mode { get; set; } = "single_put";

	[JsonPropertyName("key")]
	public string? Key { get; set; }

	[JsonPropertyName("upload_id")]
	public string? UploadId { get; set; }

	[JsonPropertyName("part_size_bytes")]
	public long? PartSizeBytes { get; set; }

	[JsonPropertyName("sha256")]
	public string? Sha256 { get; set; }

	[JsonPropertyName("parts")]
	public List<UploadPartManifest> Parts { get; set; } = new List<UploadPartManifest>();
}
