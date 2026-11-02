using System;

namespace KuroWindows.RecorderCore.Segments;

internal readonly record struct SegmentManifestLoadResult(SegmentManifest? Manifest, bool Exists, bool Corrupt, DateTime LastWriteUtc, Exception? ReadError = null);
