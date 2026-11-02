using System.Collections.Generic;

namespace KuroWindows.RecorderCore.Capture;

public sealed record DisplayTopologyPolicyResult(DisplayTopologyMode Mode, int TopologyEpoch, int PhysicalDisplayCount, int LogicalDisplayCount, string TopologySignature, string? UnsupportedReason, AdaptiveCaptureProfile Profile, IReadOnlyList<CaptureDisplaySettings> Displays)
{
	public bool IsSupported => UnsupportedReason == null;

	public string AdaptiveProfileReason { get; init; } = "default";

	public int AdaptiveProfileFailureCount { get; init; }
}
