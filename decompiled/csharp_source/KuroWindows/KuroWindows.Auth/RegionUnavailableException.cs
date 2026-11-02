using System;

namespace KuroWindows.Auth;

internal sealed class RegionUnavailableException : Exception
{
	public RegionUnavailableException(string text, Exception? innerException = null)
		: base(text, innerException)
	{
	}
}
