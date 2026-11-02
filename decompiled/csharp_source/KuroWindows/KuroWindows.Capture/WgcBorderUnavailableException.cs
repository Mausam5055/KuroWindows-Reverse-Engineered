using System;

namespace KuroWindows.Capture;

internal sealed class WgcBorderUnavailableException : NotSupportedException
{
	public string Reason { get; }

	public WgcBorderUnavailableException(string reason, string message)
		: base(message)
	{
		Reason = reason;
	}
}
