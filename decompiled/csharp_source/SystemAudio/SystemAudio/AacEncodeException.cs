using System;

namespace SystemAudio;

internal sealed class AacEncodeException(uint error) : Exception($"aacEncEncode failed error=0x{error:X4}")
{
	public uint Error { get; } = error;
}
