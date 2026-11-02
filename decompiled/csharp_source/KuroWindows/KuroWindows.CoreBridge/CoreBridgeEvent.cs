using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeEvent
{
	[JsonPropertyName("type")]
	public string Type { get; init; } = "snapshot";

	[JsonPropertyName("snapshot")]
	public CoreBridgeSnapshot? Snapshot { get; init; }

	[JsonPropertyName("communications")]
	public JsonElement? Communications { get; init; }

	[JsonPropertyName("session_generation")]
	public int? SessionGeneration { get; init; }

	[JsonPropertyName("owner_user_id")]
	public string? OwnerUserId { get; init; }

	[JsonPropertyName("url")]
	public string? Url { get; init; }
}
