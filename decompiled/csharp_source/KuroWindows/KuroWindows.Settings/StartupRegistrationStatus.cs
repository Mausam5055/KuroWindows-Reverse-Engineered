namespace KuroWindows.Settings;

public sealed record StartupRegistrationStatus(bool DesiredEnabled, bool IsRegistered, string? ActualCommand, string? ExpectedCommand, string? Error);
