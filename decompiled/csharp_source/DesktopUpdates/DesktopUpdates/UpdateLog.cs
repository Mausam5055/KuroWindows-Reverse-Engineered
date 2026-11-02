using System;
using System.Runtime.CompilerServices;

namespace DesktopUpdates;

internal sealed class UpdateLog
{
	private readonly UpdateLogSink _line;

	private readonly UpdateLogSink _localLine;

	private readonly Func<Exception, string> _summary;

	private UpdateLog(UpdateLogSink line, UpdateLogSink localLine, Func<Exception, string> summary)
	{
		_line = line;
		_localLine = localLine;
		_summary = summary;
	}

	public static UpdateLog From(UpdateOptions options)
	{
		return new UpdateLog(options.Log, options.LocalLog, options.ExceptionSummary);
	}

	public void Update(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		try
		{
			_line(msg, memberName, filePath, lineNumber);
		}
		catch
		{
		}
	}

	public void LocalOnly(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		try
		{
			_localLine(msg, memberName, filePath, lineNumber);
		}
		catch
		{
		}
	}

	public string ExceptionSummary(Exception ex)
	{
		try
		{
			return _summary(ex);
		}
		catch
		{
			return ex.GetType().Name + ": " + ex.Message;
		}
	}
}
