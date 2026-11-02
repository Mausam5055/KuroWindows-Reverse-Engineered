using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeError([property: JsonPropertyName("code")] string Code, [property: JsonPropertyName("message")] string Message);
