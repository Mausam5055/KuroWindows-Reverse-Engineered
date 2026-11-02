namespace KuroWindows.RecorderCore.Capture;

internal readonly record struct AdaptiveCaptureProfileSelection(AdaptiveCaptureProfile Profile, string Reason, int ConsecutiveFailures);
