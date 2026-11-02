using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeDiskState
{
	[JsonPropertyName("available_bytes")]
	public long AvailableBytes { get; init; }

	[JsonPropertyName("minimum_required_bytes")]
	public long MinimumRequiredBytes { get; init; } = AppConfig.MinimumDiskSpaceBytes;

	[JsonPropertyName("low_disk")]
	public bool LowDisk { get; init; }
}
