using System;
using System.Runtime.InteropServices;

namespace SystemAudio;

internal readonly struct AudioRenderClient(nint nativePointer)
{
	public nint NativePointer { get; } = nativePointer;

	public bool IsNull => NativePointer == IntPtr.Zero;

	private unsafe void** Vtbl => *(void***)NativePointer;

	public unsafe int GetBuffer(uint frames, out byte* data)
	{
		byte* ptr = default;
		int result = ((delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)Vtbl[3])(NativePointer, frames, &ptr);
		data = ptr;
		return result;
	}

	public unsafe int ReleaseBuffer(uint frames, uint flags)
	{
		return ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Vtbl[4])(NativePointer, frames, flags);
	}

	public void Release()
	{
		if (!IsNull)
		{
			Marshal.Release(NativePointer);
		}
	}
}
