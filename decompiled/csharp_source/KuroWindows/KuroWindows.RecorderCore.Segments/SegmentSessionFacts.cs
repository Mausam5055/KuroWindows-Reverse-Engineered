using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentSessionFacts
{
	[JsonPropertyName("v")]
	public int V { get; set; } = 1;

	[JsonPropertyName("input")]
	public SegmentInputFacts? Input { get; set; }

	[JsonPropertyName("session")]
	public SegmentSessionEnvironment? Session { get; set; }

	[JsonPropertyName("displays")]
	public List<SegmentDisplayFact>? Displays { get; set; }
}
