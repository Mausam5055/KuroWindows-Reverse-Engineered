using System.Drawing;
using System.Windows.Forms;

namespace KuroWindows.RecorderCore.Capture;

public sealed record CaptureDisplaySettings(int LogicalDisplayIndex, int OriginalScreenIndex, CaptureDisplayRole Role, Screen Screen, Rectangle NativeBounds, int CaptureWidth, int CaptureHeight, int CaptureFps, bool IsResolutionThrottled, string ResolutionMode);
