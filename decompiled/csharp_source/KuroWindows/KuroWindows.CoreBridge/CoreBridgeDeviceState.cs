using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeDeviceState
{
	[JsonPropertyName("id")]
	public string? Id { get; init; }
}
