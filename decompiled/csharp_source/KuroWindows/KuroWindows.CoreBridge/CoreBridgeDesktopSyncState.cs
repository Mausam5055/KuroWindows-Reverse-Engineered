using System;
using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeDesktopSyncState
{
	[JsonPropertyName("status")]
	public string Status { get; init; } = "not_started";

	[JsonPropertyName("has_config")]
	public bool HasConfig { get; init; }

	[JsonPropertyName("config_version")]
	public long ConfigVersion { get; init; }

	[JsonPropertyName("last_seen_at")]
	public DateTimeOffset? LastSeenAt { get; init; }

	[JsonPropertyName("synced_at")]
	public DateTimeOffset? SyncedAt { get; init; }

	[JsonPropertyName("next_sync_after_seconds")]
	public int NextSyncAfterSeconds { get; init; }

	[JsonPropertyName("last_error")]
	public string? LastError { get; init; }
}
