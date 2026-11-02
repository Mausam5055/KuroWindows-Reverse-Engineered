using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentAudioMixFormat
{
	[JsonPropertyName("rate")]
	public int Rate { get; set; }

	[JsonPropertyName("channels")]
	public int Channels { get; set; }

	[JsonPropertyName("bits")]
	public int Bits { get; set; }
}
