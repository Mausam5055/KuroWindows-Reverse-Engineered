using System;
using System.Runtime.InteropServices;

namespace SystemAudio;

internal readonly struct AudioCaptureClient(nint nativePointer)
{
	public nint NativePointer { get; } = nativePointer;

	public bool IsNull => NativePointer == IntPtr.Zero;

	private unsafe void** Vtbl => *(void***)NativePointer;

	public unsafe int GetBuffer(out byte* data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition)
	{
		byte* ptr = default;
		uint num = default;
		uint num2 = default;
		ulong num3 = default;
		ulong num4 = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Vtbl[3])(NativePointer, &ptr, &num, &num2, &num3, &num4);
		data = ptr;
		frames = num;
		flags = num2;
		devicePosition = num3;
		qpcPosition = num4;
		return result;
	}

	public unsafe int ReleaseBuffer(uint frames)
	{
		return ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtbl[4])(NativePointer, frames);
	}

	public unsafe int GetNextPacketSize(out uint frames)
	{
		uint num = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl[5])(NativePointer, &num);
		frames = num;
		return result;
	}

	public void Release()
	{
		if (!IsNull)
		{
			Marshal.Release(NativePointer);
		}
	}
}
