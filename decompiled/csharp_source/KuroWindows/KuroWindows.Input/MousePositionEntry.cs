namespace KuroWindows.Input;

public sealed record MousePositionEntry(int X, int Y, int Screen, long TimestampMs) : IHasTimestamp;
