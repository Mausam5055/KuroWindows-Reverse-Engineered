using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentInputExtraInfo
{
	[JsonPropertyName("tag")]
	public string Tag { get; set; } = "";

	[JsonPropertyName("n")]
	public long N { get; set; }
}
