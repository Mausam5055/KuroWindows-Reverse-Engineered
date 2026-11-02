using System;

namespace KuroWindows.Capture;

internal class CaptureEncoderStallException : InvalidOperationException
{
	public int DisplayIndex { get; }

	public virtual bool LatchesWireProfile => true;

	public CaptureEncoderStallException(int displayIndex, string reason)
		: base(reason)
	{
		DisplayIndex = displayIndex;
	}
}
