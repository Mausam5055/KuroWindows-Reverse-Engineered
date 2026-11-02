using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionFactsMetadata
{
	[JsonPropertyName("v")]
	public int V { get; set; } = 1;

	[JsonPropertyName("input")]
	public SessionFactsInputMetadata? Input { get; set; }

	[JsonPropertyName("session")]
	public SessionFactsSessionMetadata? Session { get; set; }

	[JsonPropertyName("displays")]
	public List<SessionFactsDisplayMetadata>? Displays { get; set; }
}
