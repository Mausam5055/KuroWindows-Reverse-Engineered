namespace KuroWindows.Input;

public sealed record ActiveAppSnapshot(string Name, string? ExeName, int Pid, string? WindowTitle, string? BrowserUrl, bool? IsFullScreen);
