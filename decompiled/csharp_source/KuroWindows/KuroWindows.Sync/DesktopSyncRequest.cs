using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopSyncRequest
{
	[JsonPropertyName("device_id")]
	public string DeviceId { get; init; } = "";

	[JsonPropertyName("known_config_version")]
	public long KnownConfigVersion { get; init; }

	[JsonPropertyName("app_version")]
	public string AppVersion { get; init; } = AppConfig.AppVersion;

	[JsonPropertyName("supports_telemetry")]
	public bool SupportsTelemetry { get; init; } = true;
}
