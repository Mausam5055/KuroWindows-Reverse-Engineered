using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeUpdateState
{
	[JsonPropertyName("status")]
	public string Status { get; init; } = "idle";

	[JsonPropertyName("required")]
	public bool Required { get; init; }

	[JsonPropertyName("available_version")]
	public string? AvailableVersion { get; init; }

	[JsonPropertyName("last_error")]
	public string? LastError { get; init; }
}
