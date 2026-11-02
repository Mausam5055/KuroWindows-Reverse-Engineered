using System.Drawing;
using System.Windows.Forms;

namespace KuroWindows.RecorderCore.Capture;

internal static class CaptureDisplayKey
{
	public static string For(Screen screen)
	{
		Rectangle bounds = screen.Bounds;
		return $"{screen.DeviceName}:{bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}";
	}
}
