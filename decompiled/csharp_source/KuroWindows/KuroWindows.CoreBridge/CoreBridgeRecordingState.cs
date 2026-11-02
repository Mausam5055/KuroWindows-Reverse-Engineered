using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeRecordingState
{
	[JsonPropertyName("state")]
	public string State { get; init; } = SessionState.Idle.ToString();

	[JsonPropertyName("transition")]
	public string? Transition { get; init; }

	[JsonPropertyName("expected")]
	public bool Expected { get; init; }

	[JsonPropertyName("manual_controls_allowed")]
	public bool ManualControlsAllowed { get; init; }

	[JsonPropertyName("current_segment_index")]
	public int CurrentSegmentIndex { get; init; }

	[JsonPropertyName("has_active_session")]
	public bool HasActiveSession { get; init; }

	[JsonPropertyName("unsupported_topology_reason")]
	public string? UnsupportedTopologyReason { get; init; }
}
