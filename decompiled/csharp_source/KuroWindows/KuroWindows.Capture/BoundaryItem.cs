using KuroWindows.Input;

namespace KuroWindows.Capture;

internal sealed record BoundaryItem(SegmentSet? Closing, SegmentSet? Opening, long BoundaryWallMs, InputStateSnapshot? Held);
