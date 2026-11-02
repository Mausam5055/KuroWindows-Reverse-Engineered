using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentManifestError
{
	[JsonPropertyName("domain")]
	public string? Domain { get; set; }

	[JsonPropertyName("code")]
	public int? Code { get; set; }

	[JsonPropertyName("name")]
	public string? Name { get; set; }

	[JsonPropertyName("description")]
	public string? Description { get; set; }
}
