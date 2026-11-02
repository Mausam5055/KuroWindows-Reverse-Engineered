namespace KuroWindows.Input.Uia;

internal readonly record struct UiaProbeResponse(int Seq, UiaProbeStatus Status, bool IsSecure, string? SelectedText);
