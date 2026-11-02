namespace KuroWindows.Input;

public sealed record ScrollEntry(double Dx, double Dy, int Screen, long TimestampMs, string? Src = null) : IHasTimestamp;
