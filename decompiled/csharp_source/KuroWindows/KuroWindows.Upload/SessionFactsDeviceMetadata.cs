using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionFactsDeviceMetadata
{
	[JsonPropertyName("vid")]
	public string? Vid { get; set; }

	[JsonPropertyName("pid")]
	public string? Pid { get; set; }

	[JsonPropertyName("kind")]
	public string Kind { get; set; } = "hid";

	[JsonPropertyName("name")]
	public string? Name { get; set; }
}
