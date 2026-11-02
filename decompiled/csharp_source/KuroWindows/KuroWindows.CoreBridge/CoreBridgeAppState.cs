using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeAppState
{
	[JsonPropertyName("name")]
	public string Name { get; init; } = AppConfig.LocalAppName;

	[JsonPropertyName("version")]
	public string Version { get; init; } = AppConfig.AppVersion;

	[JsonPropertyName("platform")]
	public string Platform { get; init; } = "windows";
}
