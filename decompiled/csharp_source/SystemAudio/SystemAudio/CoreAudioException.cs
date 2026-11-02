using System;

namespace SystemAudio;

internal sealed class CoreAudioException : Exception
{
	public CoreAudioException(string call, int hr)
		: base($"{call} failed hr=0x{hr:X8}")
	{
		HResult = hr;
	}
}
