namespace KuroWindows.Auth;

public record AuthStateChangedEventArgs(AuthState State, UserSessionSummary? Session = null, string? BanReason = null, string? LinkedEmail = null);
