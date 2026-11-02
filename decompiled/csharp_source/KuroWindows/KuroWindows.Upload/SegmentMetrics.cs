using System;
using System.Collections.Generic;

namespace KuroWindows.Upload;

public record SegmentMetrics(long DurationMs, long IdleMs, DateTimeOffset? CapturedAt, IReadOnlyList<SegmentFile> AllFiles);
