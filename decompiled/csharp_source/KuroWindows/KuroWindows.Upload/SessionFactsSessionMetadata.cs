using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionFactsSessionMetadata
{
	[JsonPropertyName("remote")]
	public bool Remote { get; set; }

	[JsonPropertyName("connect_state")]
	public string? ConnectState { get; set; }

	[JsonPropertyName("remote_tools")]
	public List<string>? RemoteTools { get; set; }
}
