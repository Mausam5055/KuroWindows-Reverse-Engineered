namespace KuroWindows.RecorderCore.Capture;

public sealed record AdaptiveCaptureProfile(int Index, string Name, int PrimaryFps, int SecondaryFps, bool SecondaryDownscaleTo1080Floor, bool PrimaryOnly);
