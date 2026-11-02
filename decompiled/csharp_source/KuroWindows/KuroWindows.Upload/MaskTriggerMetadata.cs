using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class MaskTriggerMetadata
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("detail")]
	public string? Detail { get; set; }
}
