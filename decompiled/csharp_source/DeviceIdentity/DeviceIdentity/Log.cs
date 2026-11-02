using System.Runtime.CompilerServices;

namespace DeviceIdentity;

internal static class Log
{
	public static void Auth(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		DeviceIdentityLogSink authSink = DeviceIdentityRuntime.AuthSink;
		if (authSink == null)
		{
			return;
		}
		try
		{
			authSink(msg, memberName, filePath, lineNumber);
		}
		catch
		{
		}
	}
}
