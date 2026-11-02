using System;

namespace KuroWindows.Capture;

internal sealed class CaptureAccessDeniedException : Exception
{
	public CaptureAccessDeniedException(string message, Exception? innerException = null)
		: base(message, innerException)
	{
		HResult = -2147024891;
	}
}
