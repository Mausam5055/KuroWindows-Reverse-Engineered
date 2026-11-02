using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeSnapshot
{
	[JsonPropertyName("app")]
	public CoreBridgeAppState App { get; init; } = new CoreBridgeAppState();

	[JsonPropertyName("auth")]
	public CoreBridgeAuthState Auth { get; init; } = new CoreBridgeAuthState();

	[JsonPropertyName("device")]
	public CoreBridgeDeviceState Device { get; init; } = new CoreBridgeDeviceState();

	[JsonPropertyName("desktop_sync")]
	public CoreBridgeDesktopSyncState DesktopSync { get; init; } = new CoreBridgeDesktopSyncState();

	[JsonPropertyName("recording")]
	public CoreBridgeRecordingState Recording { get; init; } = new CoreBridgeRecordingState();

	[JsonPropertyName("masking")]
	public CoreBridgeMaskingState Masking { get; init; } = new CoreBridgeMaskingState();

	[JsonPropertyName("upload")]
	public CoreBridgeUploadState Upload { get; init; } = new CoreBridgeUploadState();

	[JsonPropertyName("disk")]
	public CoreBridgeDiskState Disk { get; init; } = new CoreBridgeDiskState();

	[JsonPropertyName("update")]
	public CoreBridgeUpdateState Update { get; init; } = new CoreBridgeUpdateState();

	[JsonPropertyName("settings")]
	public CoreBridgeSettingsState Settings { get; init; } = new CoreBridgeSettingsState();
}
