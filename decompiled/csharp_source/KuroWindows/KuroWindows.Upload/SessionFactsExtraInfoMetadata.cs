using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionFactsExtraInfoMetadata
{
	[JsonPropertyName("tag")]
	public string Tag { get; set; } = "";

	[JsonPropertyName("n")]
	public long N { get; set; }
}
