using System.Runtime.CompilerServices;

namespace SystemAudio;

internal static class Log
{
	public static void Capture(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Forward(SystemAudioRuntime.CaptureSink, msg, memberName, filePath, lineNumber);
	}

	public static void Local(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Forward(SystemAudioRuntime.LocalSink, msg, memberName, filePath, lineNumber);
	}

	private static void Forward(SystemAudioLogSink? sink, string msg, string memberName, string filePath, int lineNumber)
	{
		if (sink == null)
		{
			return;
		}
		try
		{
			sink(msg, memberName, filePath, lineNumber);
		}
		catch
		{
		}
	}
}
