using System;

namespace KuroWindows.Auth;

internal sealed class SignupPausedException : Exception
{
	public SignupPausedException(Exception? innerException = null)
		: base("Sign-ups are currently paused.", innerException)
	{
	}
}
