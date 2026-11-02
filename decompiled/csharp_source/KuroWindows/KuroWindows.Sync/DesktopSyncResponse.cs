using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopSyncResponse
{
	[JsonPropertyName("server_time")]
	public DateTimeOffset ServerTime { get; init; }

	[JsonPropertyName("last_seen_at")]
	public DateTimeOffset LastSeenAt { get; init; }

	[JsonPropertyName("config_version")]
	public long ConfigVersion { get; init; }

	[JsonPropertyName("config_changed")]
	public bool ConfigChanged { get; init; }

	[JsonPropertyName("next_sync_after_seconds")]
	public int NextSyncAfterSeconds { get; init; } = 90;

	[JsonPropertyName("config")]
	public DesktopConfig? Config { get; init; }

	[JsonPropertyName("communications")]
	public JsonElement Communications { get; init; }
}
