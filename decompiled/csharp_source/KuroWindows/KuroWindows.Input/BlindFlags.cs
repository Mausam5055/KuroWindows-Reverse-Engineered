namespace KuroWindows.Input;

internal readonly record struct BlindFlags(bool ForegroundElevated, int ForegroundPid, string ForegroundApp, bool DesktopSwitched, bool SecureDesktop, bool SessionNotInteractive, bool SessionDisconnected);
