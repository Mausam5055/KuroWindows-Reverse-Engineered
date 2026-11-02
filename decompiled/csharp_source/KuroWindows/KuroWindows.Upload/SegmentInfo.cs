using System.Collections.Generic;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Upload;

public record SegmentInfo(string SessionDir, string ClientSessionId, int SegmentIndex, IReadOnlyList<DisplayFiles> Displays, SegmentManifestState ManifestState, bool CaptureSurfaceStale, bool WasRecovered, long StartedWallMs = 0L);
