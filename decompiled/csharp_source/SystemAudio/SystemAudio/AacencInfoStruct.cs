namespace SystemAudio;

internal struct AacencInfoStruct
{
	public uint MaxOutBufBytes;

	public uint MaxAncBytes;

	public uint InBufFillLevel;

	public uint InputChannels;

	public uint FrameLength;

	public uint Delay;

	public uint DelayCore;

	public unsafe fixed byte ConfBuf[64];

	public uint ConfSize;
}
