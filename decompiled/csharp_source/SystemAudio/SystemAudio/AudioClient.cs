using System;
using System.Runtime.InteropServices;

namespace SystemAudio;

internal readonly struct AudioClient(nint nativePointer)
{
	public nint NativePointer { get; } = nativePointer;

	public bool IsNull => NativePointer == IntPtr.Zero;

	private unsafe void** Vtbl => *(void***)NativePointer;

	public unsafe int Initialize(int shareMode, uint streamFlags, long bufferDurationHns, long periodicityHns, void* format, Guid* sessionGuid)
	{
		return ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, void*, Guid*, int>)Vtbl[3])(NativePointer, shareMode, streamFlags, bufferDurationHns, periodicityHns, format, sessionGuid);
	}

	public unsafe int GetBufferSize(out uint frames)
	{
		uint num = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl[4])(NativePointer, &num);
		frames = num;
		return result;
	}

	public unsafe int GetCurrentPadding(out uint frames)
	{
		uint num = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl[6])(NativePointer, &num);
		frames = num;
		return result;
	}

	public unsafe int GetMixFormat(out nint format)
	{
		nint num = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl[8])(NativePointer, &num);
		format = num;
		return result;
	}

	public unsafe int Start()
	{
		return ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl[10])(NativePointer);
	}

	public unsafe int Stop()
	{
		return ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl[11])(NativePointer);
	}

	public unsafe int SetEventHandle(nint eventHandle)
	{
		return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtbl[13])(NativePointer, eventHandle);
	}

	public unsafe int GetService(Guid iid, out nint service)
	{
		nint num = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl[14])(NativePointer, &iid, &num);
		service = num;
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
