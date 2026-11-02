using System;

namespace KuroWindows.Capture;

internal delegate void RowWrittenHandler(ReadOnlySpan<byte> line, long seq);
