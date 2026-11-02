namespace KuroWindows.Capture;

public sealed record VideoBitrateSnapshot(double BitsPerPixelPerSecond, double MinMbps, double MaxMbps, string Source);
