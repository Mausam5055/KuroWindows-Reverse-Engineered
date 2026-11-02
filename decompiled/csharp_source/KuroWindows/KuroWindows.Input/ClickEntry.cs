namespace KuroWindows.Input;

public sealed record ClickEntry(string Button, bool IsDown, int X, int Y, int Screen, long TimestampMs) : IHasTimestamp;
