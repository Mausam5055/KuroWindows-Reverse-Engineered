using System.Runtime.InteropServices;

namespace SystemAudio;

internal static class FdkAacInterop
{
	public const string Library = "fdk-aac.dll";

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public static extern uint aacEncOpen(out nint phAacEncoder, uint encModules, uint maxChannels);

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public static extern uint aacEncClose(ref nint phAacEncoder);

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public static extern uint aacEncoder_SetParam(nint hAacEncoder, AacencParam param, uint value);

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public static extern uint aacEncoder_GetParam(nint hAacEncoder, AacencParam param);

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public unsafe static extern uint aacEncInfo(nint hAacEncoder, AacencInfoStruct* pInfo);

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public unsafe static extern uint aacEncEncode(nint hAacEncoder, AacencBufDesc* inBufDesc, AacencBufDesc* outBufDesc, AacencInArgs* inargs, AacencOutArgs* outargs);

	[DllImport("fdk-aac.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
	public unsafe static extern uint aacEncGetLibInfo(LibInfo* info);
}
