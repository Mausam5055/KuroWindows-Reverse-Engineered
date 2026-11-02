using System;
using System.Runtime.InteropServices;

namespace KuroWindows.RecorderCore.Capture;

internal readonly record struct PadCursorObservation(bool Known, int X, int Y, nint SpriteHandle, bool Visible = false, bool Suppressed = false, nint RawSprite = 0)
{
	private struct CursorInfoNative
	{
		public int cbSize;

		public int flags;

		public nint hCursor;

		public int ptX;

		public int ptY;
	}

	public static PadCursorObservation Unknown => default;

	private const int CursorShowing = 1;

	private const int CursorSuppressed = 2;

	public static PadCursorObservation At(int x, int y, nint spriteHandle)
	{
		return new PadCursorObservation(Known: true, x, y, spriteHandle);
	}

	public static PadCursorObservation WithSystemSprite(int x, int y)
	{
		CursorInfoNative pci = new CursorInfoNative
		{
			cbSize = Marshal.SizeOf<CursorInfoNative>()
		};
		nint spriteHandle = IntPtr.Zero;
		bool flag = false;
		bool suppressed = false;
		nint rawSprite = IntPtr.Zero;
		try
		{
			if (GetCursorInfo(ref pci))
			{
				flag = (pci.flags & 1) != 0;
				suppressed = (pci.flags & 2) != 0;
				rawSprite = pci.hCursor;
				if (flag)
				{
					spriteHandle = pci.hCursor;
				}
			}
		}
		catch
		{
		}
		return new PadCursorObservation(Known: true, x, y, spriteHandle, flag, suppressed, rawSprite);
	}

	[DllImport("user32.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorInfo(ref CursorInfoNative pci);
}
