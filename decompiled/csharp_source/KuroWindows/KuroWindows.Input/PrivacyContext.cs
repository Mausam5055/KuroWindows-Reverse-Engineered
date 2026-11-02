namespace KuroWindows.Input;

public sealed record PrivacyContext(string Type, string? Detail, string ExeName, string WindowTitle, string AppName, int DisplayIndex, string? BrowserUrl);
