using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SegmentEndMetadata
{
	[JsonPropertyName("reason")]
	public string? Reason { get; set; }

	[JsonPropertyName("mask_trigger")]
	public MaskTriggerMetadata? MaskTrigger { get; set; }
}
