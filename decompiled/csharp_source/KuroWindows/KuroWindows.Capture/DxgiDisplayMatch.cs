namespace KuroWindows.Capture;

internal readonly record struct DxgiDisplayMatch(int Score, string Reason, bool DeviceNameMatches, bool BoundsMatch, bool DisplayConfigLuidMatches);
