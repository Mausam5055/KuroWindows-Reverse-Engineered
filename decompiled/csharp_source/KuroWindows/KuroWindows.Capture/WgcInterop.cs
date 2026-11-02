using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ABI.Windows.Graphics.Capture;
using Vortice.Direct3D11;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace KuroWindows.Capture;

internal static class WgcInterop
{
	private readonly struct NativeRect(Rectangle rectangle)
	{
		public readonly int Left = rectangle.Left;

		public readonly int Top = rectangle.Top;

		public readonly int Right = rectangle.Right;

		public readonly int Bottom = rectangle.Bottom;
	}

	[ComImport]
	[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IGraphicsCaptureItemInterop
	{
		[PreserveSig]
		int CreateForWindow(nint window, [In] ref Guid iid, out nint result);

		[PreserveSig]
		int CreateForMonitor(nint monitor, [In] ref Guid iid, out nint result);
	}

	[ComImport]
	[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDirect3DDxgiInterfaceAccess
	{
		void GetInterface([In] ref Guid iid, out nint p);
	}

	private const uint MonitorDefaultToNearest = 2u;

	private static readonly Guid GraphicsCaptureItemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

	[DllImport("user32.dll")]
	private static extern nint MonitorFromRect([In] ref NativeRect rect, uint flags);

	[DllImport("d3d11.dll", ExactSpelling = true)]
	private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

	public static IDirect3DDevice CreateWinRtDevice(nint dxgiDevicePtr)
	{
		Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out var graphicsDevice));
		try
		{
			return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
		}
		finally
		{
			if (graphicsDevice != IntPtr.Zero)
			{
				Marshal.Release(graphicsDevice);
			}
		}
	}

	[DllImport("combase.dll", ExactSpelling = true)]
	private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

	[DllImport("combase.dll", ExactSpelling = true)]
	private static extern int WindowsDeleteString(nint hstring);

	[DllImport("combase.dll", ExactSpelling = true)]
	private static extern int RoGetActivationFactory(nint activatableClassId, [In] ref Guid iid, out nint factory);

	public static Windows.Graphics.Capture.GraphicsCaptureItem CreateItemForMonitor(Screen screen)
	{
		NativeRect rect = new NativeRect(screen.Bounds);
		nint num = MonitorFromRect(ref rect, 2u);
		if (num == IntPtr.Zero)
		{
			throw new InvalidOperationException("Could not resolve HMONITOR for " + screen.DeviceName + ".");
		}
		string text = "Windows.Graphics.Capture.GraphicsCaptureItem";
		nint hstring = IntPtr.Zero;
		nint factory = IntPtr.Zero;
		nint result = IntPtr.Zero;
		try
		{
			Marshal.ThrowExceptionForHR(WindowsCreateString(text, text.Length, out hstring));
			Guid iid = typeof(IGraphicsCaptureItemInterop).GUID;
			Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out factory));
			IGraphicsCaptureItemInterop graphicsCaptureItemInterop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
			Guid iid2 = GraphicsCaptureItemIid;
			Marshal.ThrowExceptionForHR(graphicsCaptureItemInterop.CreateForMonitor(num, ref iid2, out result));
			if (result == IntPtr.Zero)
			{
				throw new InvalidOperationException("WGC CreateForMonitor returned null for " + screen.DeviceName + ".");
			}
			return ABI.Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi(result);
		}
		finally
		{
			if (result != IntPtr.Zero)
			{
				Marshal.Release(result);
			}
			if (factory != IntPtr.Zero)
			{
				Marshal.Release(factory);
			}
			if (hstring != IntPtr.Zero)
			{
				WindowsDeleteString(hstring);
			}
		}
	}

	public unsafe static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
	{
		nint pUnk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
		try
		{
			Guid iid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
			Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pUnk, in iid, out var ppv));
			try
			{
				Guid gUID = typeof(ID3D11Texture2D).GUID;
				nint nativePtr = default;
				Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(IntPtr*)((nint)(*(IntPtr*)ppv) + (nint)3 * (nint)sizeof(void*))))(ppv, &gUID, &nativePtr));
				return new ID3D11Texture2D(nativePtr);
			}
			finally
			{
				Marshal.Release(ppv);
			}
		}
		finally
		{
			Marshal.Release(pUnk);
		}
	}
}
