namespace SystemAudio;

internal struct AacencBufDesc
{
	public int NumBufs;

	public unsafe void** Bufs;

	public unsafe int* BufferIdentifiers;

	public unsafe int* BufSizes;

	public unsafe int* BufElSizes;
}
