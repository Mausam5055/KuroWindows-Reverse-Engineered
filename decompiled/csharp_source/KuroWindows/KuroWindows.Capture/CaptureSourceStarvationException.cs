using System;

namespace KuroWindows.Capture;

internal sealed class CaptureSourceStarvationException : InvalidOperationException
{
	public int DisplayIndex { get; }

	public CaptureSourceStarvationException(int displayIndex, string reason)
		: base(reason)
	{
		DisplayIndex = displayIndex;
	}
}
