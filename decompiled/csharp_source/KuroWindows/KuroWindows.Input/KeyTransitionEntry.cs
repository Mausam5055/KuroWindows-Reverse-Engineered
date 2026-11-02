namespace KuroWindows.Input;

public sealed record KeyTransitionEntry(uint KeyCode, string? Chars, bool IsDown, long TimestampMs, int? Screen = null) : IHasTimestamp;
