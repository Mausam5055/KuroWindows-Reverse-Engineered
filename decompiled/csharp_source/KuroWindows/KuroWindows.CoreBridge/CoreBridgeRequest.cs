using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeRequest
{
	[JsonPropertyName("id")]
	public string Id { get; init; } = Guid.NewGuid().ToString("N");

	[JsonPropertyName("method")]
	public string Method { get; init; } = "";

	[JsonPropertyName("params")]
	public JsonElement? Params { get; init; }
}
