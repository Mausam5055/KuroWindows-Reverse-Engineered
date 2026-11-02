namespace KuroWindows.Input;

public sealed record ForegroundWindowInfo(string ExeName, string AppName, string WindowTitle, int DisplayIndex, string? BrowserUrl, int Pid = 0, long Hwnd = 0L);
