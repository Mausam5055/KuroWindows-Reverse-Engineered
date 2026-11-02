using System;
using System.Drawing;
using System.Runtime.InteropServices;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture;

internal sealed class CursorRenderer : IDisposable
{
	private struct CursorInfo
	{
		public int cbSize;

		public int flags;

		public nint hCursor;

		public PointStruct ptScreenPos;
	}

	private struct PointStruct
	{
		public int x;

		public int y;
	}

	private struct IconInfo
	{
		[MarshalAs(UnmanagedType.Bool)]
		public bool fIcon;

		public int xHotspot;

		public int yHotspot;

		public nint hbmMask;

		public nint hbmColor;
	}

	private struct BitmapObject
	{
		public int bmType;

		public int bmWidth;

		public int bmHeight;

		public int bmWidthBytes;

		public short bmPlanes;

		public short bmBitsPixel;

		public nint bmBits;
	}

	private struct BitmapInfo
	{
		public BitmapInfoHeader bmiHeader;

		public uint bmiColors;
	}

	private struct BitmapInfoHeader
	{
		public uint biSize;

		public int biWidth;

		public int biHeight;

		public ushort biPlanes;

		public ushort biBitCount;

		public uint biCompression;

		public uint biSizeImage;

		public int biXPelsPerMeter;

		public int biYPelsPerMeter;

		public uint biClrUsed;

		public uint biClrImportant;
	}

	private static class CursorInterop
	{
		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool GetCursorInfo(ref CursorInfo pci);

		[DllImport("user32.dll", SetLastError = true)]
		public static extern nint CopyIcon(nint hIcon);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool GetIconInfo(nint hIcon, out IconInfo piconinfo);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, nint hbrFlickerFreeDraw, int diFlags);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DestroyIcon(nint hIcon);

		[DllImport("gdi32.dll", SetLastError = true)]
		public static extern nint CreateCompatibleDC(nint hdc);

		[DllImport("gdi32.dll", SetLastError = true)]
		public static extern nint CreateDIBSection(nint hdc, [In] ref BitmapInfo pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);

		[DllImport("gdi32.dll", SetLastError = true)]
		public static extern nint SelectObject(nint hdc, nint h);

		[DllImport("gdi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DeleteObject(nint ho);

		[DllImport("gdi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DeleteDC(nint hdc);

		[DllImport("gdi32.dll", SetLastError = true)]
		public static extern int GetObject(nint hgdiobj, int cbBuffer, out BitmapObject lpvObject);
	}

	private const int CursorShowing = 1;

	private const int DibRgbColors = 0;

	private const int BiRgb = 0;

	private const int DiNormal = 3;

	private nint _memoryDc;

	private nint _dibSection;

	private nint _selectedBitmap;

	private nint _bits;

	private int _width;

	private int _height;

	private int _stride;

	private bool _disposed;

	public bool TryOverlayCursor(byte[] destination, Rectangle outputBounds, int destinationStride, Point hotspot, in PadCursorObservation cursor, out CursorFrameSample sample, out string reason)
	{
		sample = CursorFrameSample.FromSystemCursor(hotspot, blended: false, in cursor);
		if (_disposed)
		{
			reason = "renderer-disposed";
			return false;
		}
		CursorInfo pci = new CursorInfo
		{
			cbSize = Marshal.SizeOf<CursorInfo>()
		};
		if (!CursorInterop.GetCursorInfo(ref pci) || (pci.flags & 1) == 0 || pci.hCursor == IntPtr.Zero)
		{
			reason = $"no-cursor-info flags={pci.flags} handle={pci.hCursor:X}";
			return false;
		}
		nint num = CursorInterop.CopyIcon(pci.hCursor);
		if (num == IntPtr.Zero)
		{
			reason = $"copy-icon-failed err={Marshal.GetLastWin32Error()}";
			return false;
		}
		try
		{
			if (!CursorInterop.GetIconInfo(num, out var piconinfo))
			{
				reason = $"get-icon-info-failed err={Marshal.GetLastWin32Error()}";
				return false;
			}
			try
			{
				Size cursorSize = GetCursorSize(piconinfo);
				if (cursorSize.Width <= 0 || cursorSize.Height <= 0)
				{
					reason = $"invalid-icon-size size={cursorSize.Width}x{cursorSize.Height}";
					return false;
				}
				EnsureSurface(cursorSize.Width, cursorSize.Height);
				ClearSurface();
				if (!CursorInterop.DrawIconEx(_memoryDc, 0, 0, num, cursorSize.Width, cursorSize.Height, 0u, IntPtr.Zero, 3))
				{
					reason = $"draw-icon-failed err={Marshal.GetLastWin32Error()}";
					return false;
				}
				int num2 = hotspot.X - piconinfo.xHotspot;
				int num3 = hotspot.Y - piconinfo.yHotspot;
				if (!BlendSurface(destination, destinationStride, outputBounds, num2, num3, cursorSize.Width, cursorSize.Height))
				{
					reason = $"fallback-offscreen pos={num2},{num3} size={cursorSize.Width}x{cursorSize.Height}";
					return false;
				}
				sample = CursorFrameSample.FromSystemCursor(hotspot, blended: true, in cursor);
				reason = $"fallback-blended pos={num2},{num3} size={cursorSize.Width}x{cursorSize.Height}";
				return true;
			}
			finally
			{
				if (piconinfo.hbmColor != IntPtr.Zero)
				{
					CursorInterop.DeleteObject(piconinfo.hbmColor);
				}
				if (piconinfo.hbmMask != IntPtr.Zero)
				{
					CursorInterop.DeleteObject(piconinfo.hbmMask);
				}
			}
		}
		finally
		{
			CursorInterop.DestroyIcon(num);
		}
	}

	public bool TryRenderCursorSprite(ref byte[]? spriteBuffer, out int spriteWidth, out int spriteHeight, out Point iconHotspot, out string reason)
	{
		spriteWidth = 0;
		spriteHeight = 0;
		iconHotspot = Point.Empty;
		if (_disposed)
		{
			reason = "renderer-disposed";
			return false;
		}
		CursorInfo pci = new CursorInfo
		{
			cbSize = Marshal.SizeOf<CursorInfo>()
		};
		if (!CursorInterop.GetCursorInfo(ref pci) || (pci.flags & 1) == 0 || pci.hCursor == IntPtr.Zero)
		{
			reason = $"no-cursor-info flags={pci.flags} handle={pci.hCursor:X}";
			return false;
		}
		nint num = CursorInterop.CopyIcon(pci.hCursor);
		if (num == IntPtr.Zero)
		{
			reason = $"copy-icon-failed err={Marshal.GetLastWin32Error()}";
			return false;
		}
		try
		{
			if (!CursorInterop.GetIconInfo(num, out var piconinfo))
			{
				reason = $"get-icon-info-failed err={Marshal.GetLastWin32Error()}";
				return false;
			}
			try
			{
				Size cursorSize = GetCursorSize(piconinfo);
				if (cursorSize.Width <= 0 || cursorSize.Height <= 0)
				{
					reason = $"invalid-icon-size size={cursorSize.Width}x{cursorSize.Height}";
					return false;
				}
				EnsureSurface(cursorSize.Width, cursorSize.Height);
				ClearSurface();
				if (!CursorInterop.DrawIconEx(_memoryDc, 0, 0, num, cursorSize.Width, cursorSize.Height, 0u, IntPtr.Zero, 3))
				{
					reason = $"draw-icon-failed err={Marshal.GetLastWin32Error()}";
					return false;
				}
				int num2 = checked(_stride * _height);
				if (spriteBuffer == null || spriteBuffer.Length < num2)
				{
					spriteBuffer = new byte[num2];
				}
				Marshal.Copy(_bits, spriteBuffer, 0, num2);
				spriteWidth = cursorSize.Width;
				spriteHeight = cursorSize.Height;
				iconHotspot = new Point(piconinfo.xHotspot, piconinfo.yHotspot);
				reason = "sprite-rendered";
				return true;
			}
			finally
			{
				if (piconinfo.hbmColor != IntPtr.Zero)
				{
					CursorInterop.DeleteObject(piconinfo.hbmColor);
				}
				if (piconinfo.hbmMask != IntPtr.Zero)
				{
					CursorInterop.DeleteObject(piconinfo.hbmMask);
				}
			}
		}
		finally
		{
			CursorInterop.DestroyIcon(num);
		}
	}

	private Size GetCursorSize(IconInfo iconInfo)
	{
		nint num = ((iconInfo.hbmColor != IntPtr.Zero) ? iconInfo.hbmColor : iconInfo.hbmMask);
		if (num == IntPtr.Zero)
		{
			return Size.Empty;
		}
		BitmapObject lpvObject = default;
		if (CursorInterop.GetObject(num, Marshal.SizeOf<BitmapObject>(), out lpvObject) == 0)
		{
			return Size.Empty;
		}
		int num2 = lpvObject.bmHeight;
		if (iconInfo.hbmColor == IntPtr.Zero)
		{
			num2 /= 2;
		}
		return new Size(lpvObject.bmWidth, num2);
	}

	private void EnsureSurface(int width, int height)
	{
		if (_memoryDc == IntPtr.Zero || _width != width || _height != height)
		{
			ReleaseSurface();
			_memoryDc = CursorInterop.CreateCompatibleDC(IntPtr.Zero);
			if (_memoryDc == IntPtr.Zero)
			{
				throw new InvalidOperationException("Failed to create memory DC for cursor composition.");
			}
			BitmapInfo pbmi = new BitmapInfo
			{
				bmiHeader = new BitmapInfoHeader
				{
					biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
					biWidth = width,
					biHeight = -height,
					biPlanes = 1,
					biBitCount = 32,
					biCompression = 0u,
					biSizeImage = (uint)(width * height * 4)
				}
			};
			_dibSection = CursorInterop.CreateDIBSection(_memoryDc, ref pbmi, 0u, out _bits, IntPtr.Zero, 0u);
			if (_dibSection == IntPtr.Zero || _bits == IntPtr.Zero)
			{
				ReleaseSurface();
				throw new InvalidOperationException("Failed to allocate cursor composition surface.");
			}
			_selectedBitmap = CursorInterop.SelectObject(_memoryDc, _dibSection);
			_width = width;
			_height = height;
			_stride = width * 4;
		}
	}

	private unsafe void ClearSurface()
	{
		new Span<byte>((void*)_bits, checked(_stride * _height)).Clear();
	}

	private unsafe bool BlendSurface(byte[] destination, int destinationStride, Rectangle outputBounds, int cursorLeft, int cursorTop, int cursorWidth, int cursorHeight)
	{
		int num = Math.Max(cursorLeft, outputBounds.Left);
		int num2 = Math.Max(cursorTop, outputBounds.Top);
		int num3 = Math.Min(cursorLeft + cursorWidth, outputBounds.Right);
		int num4 = Math.Min(cursorTop + cursorHeight, outputBounds.Bottom);
		if (num >= num3 || num2 >= num4)
		{
			return false;
		}
		int num5 = Math.Max(1, destinationStride / 4);
		int num6 = Math.Max(1, destination.Length / Math.Max(destinationStride, 1));
		double num7 = (double)num5 / (double)Math.Max(outputBounds.Width, 1);
		double num8 = (double)num6 / (double)Math.Max(outputBounds.Height, 1);
		int num9 = num - cursorLeft;
		int num10 = num2 - cursorTop;
		int num11 = Math.Clamp((int)Math.Floor((double)(num - outputBounds.Left) * num7), 0, num5);
		int num12 = Math.Clamp((int)Math.Floor((double)(num2 - outputBounds.Top) * num8), 0, num6);
		int num13 = Math.Clamp((int)Math.Ceiling((double)(num3 - outputBounds.Left) * num7), 0, num5);
		int num14 = Math.Clamp((int)Math.Ceiling((double)(num4 - outputBounds.Top) * num8), 0, num6);
		int num15 = num13 - num11;
		int num16 = num14 - num12;
		if (num15 <= 0 || num16 <= 0)
		{
			return false;
		}
		fixed (byte* ptr = destination)
		{
			byte* bits = (byte*)_bits;
			for (int i = 0; i < num16; i++)
			{
				int num17 = Math.Clamp(num10 + (int)Math.Floor((double)i / Math.Max(num8, 0.0001)), 0, cursorHeight - 1);
				byte* ptr2 = bits + num17 * _stride;
				byte* ptr3 = ptr + (num12 + i) * destinationStride + num11 * 4;
				for (int j = 0; j < num15; j++)
				{
					int num18 = Math.Clamp(num9 + (int)Math.Floor((double)j / Math.Max(num7, 0.0001)), 0, cursorWidth - 1);
					byte* ptr4 = ptr2 + num18 * 4;
					byte* ptr5 = ptr3 + j * 4;
					byte b = ptr4[3];
					switch (b)
					{
					case byte.MaxValue:
						*ptr5 = *ptr4;
						ptr5[1] = ptr4[1];
						ptr5[2] = ptr4[2];
						ptr5[3] = byte.MaxValue;
						break;
					default:
					{
						int num19 = 255 - b;
						*ptr5 = (byte)((*ptr4 * b + *ptr5 * num19) / 255);
						ptr5[1] = (byte)((ptr4[1] * b + ptr5[1] * num19) / 255);
						ptr5[2] = (byte)((ptr4[2] * b + ptr5[2] * num19) / 255);
						ptr5[3] = byte.MaxValue;
						break;
					}
					case 0:
						break;
					}
				}
			}
		}
		return true;
	}

	private void ReleaseSurface()
	{
		if (_memoryDc != IntPtr.Zero && _selectedBitmap != IntPtr.Zero)
		{
			CursorInterop.SelectObject(_memoryDc, _selectedBitmap);
			_selectedBitmap = IntPtr.Zero;
		}
		if (_dibSection != IntPtr.Zero)
		{
			CursorInterop.DeleteObject(_dibSection);
			_dibSection = IntPtr.Zero;
		}
		if (_memoryDc != IntPtr.Zero)
		{
			CursorInterop.DeleteDC(_memoryDc);
			_memoryDc = IntPtr.Zero;
		}
		_bits = IntPtr.Zero;
		_width = 0;
		_height = 0;
		_stride = 0;
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			ReleaseSurface();
			_disposed = true;
		}
	}
}
