using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed record AudioMixFormatMetadata
{
	[JsonPropertyName("rate")]
	public int Rate { get; init; }

	[JsonPropertyName("channels")]
	public int Channels { get; init; }

	[JsonPropertyName("bits")]
	public int Bits { get; init; }
}
