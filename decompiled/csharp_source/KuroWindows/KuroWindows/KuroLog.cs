using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClientTelemetry;
using SharpGen.Runtime;

namespace KuroWindows;

internal static class KuroLog
{
	private const long MaxLogBytes = 5242880L;

	private const int MaxLocalMessageChars = 4096;

	private const string LineTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

	private const int LineTimestampChars = 30;

	internal const int TailReadBytesPerFile = 32768;

	internal const int TailEntryMaxChars = 512;

	private static readonly object LocalLogLock = new object();

	internal static readonly DateTimeOffset ProcessStartedAt = CreationTimeToTheMillisecond();

	private static readonly string DefaultLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "logs");

	private static int _localWriteFailureSignaled;

	internal static string? LogDirectoryOverride { get; set; }

	private static string LogDir => LogDirectoryOverride ?? DefaultLogDir;

	private static DateTimeOffset CreationTimeToTheMillisecond()
	{
		using Process process = Process.GetCurrentProcess();
		DateTimeOffset dateTimeOffset = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
		return dateTimeOffset.AddTicks(-(dateTimeOffset.Ticks % 10000));
	}

	public static void Crash(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("crash", msg, memberName, filePath, lineNumber);
	}

	public static void App(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("app", msg, memberName, filePath, lineNumber);
	}

	public static void Auth(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("auth", msg, memberName, filePath, lineNumber);
	}

	public static void Upload(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("upload", msg, memberName, filePath, lineNumber);
	}

	public static void Sync(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("sync", msg, memberName, filePath, lineNumber);
	}

	public static void Recording(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("recording", msg, memberName, filePath, lineNumber);
	}

	public static void Update(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("update", msg, memberName, filePath, lineNumber);
	}

	public static void SystemEvent(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("system", msg, memberName, filePath, lineNumber);
	}

	public static void Capture(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		Write("capture", msg, memberName, filePath, lineNumber);
	}

	public static void LocalOnly(string category, string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		WriteLocal(category, msg, memberName, filePath, lineNumber);
	}

	internal static string ExceptionSummary(Exception ex)
	{
		string text = ex.GetType().Name + ": " + ex.Message;
		int? num = NativeHResult(ex);
		if (num.HasValue)
		{
			int valueOrDefault = num.GetValueOrDefault();
			return $"{text} hr=0x{valueOrDefault:X8}";
		}
		return text;
	}

	private static int? NativeHResult(Exception ex)
	{
		bool flag = ((ex is ExternalException || ex is SharpGenException) ? true : false);
		if (flag && ex.HResult < 0)
		{
			return ex.HResult;
		}
		if (ex is AggregateException ex2)
		{
			foreach (Exception innerException in ex2.InnerExceptions)
			{
				int? num = NativeHResult(innerException);
				if (num.HasValue)
				{
					return num.GetValueOrDefault();
				}
			}
		}
		if (ex.InnerException == null)
		{
			return null;
		}
		return NativeHResult(ex.InnerException);
	}

	private static void Write(string category, string msg, string memberName, string filePath, int lineNumber)
	{
		WriteLocal(category, msg, memberName, filePath, lineNumber);
		if (Telemetry.Level >= Gate.Verbose)
		{
			Telemetry.Verbose(category, msg, SourceFor(filePath, lineNumber));
		}
	}

	private static string SourceFor(string filePath, int lineNumber)
	{
		string text = (string.IsNullOrWhiteSpace(filePath) ? "-" : Path.GetFileName(filePath));
		if (lineNumber > 0)
		{
			return $"{text}:{lineNumber}";
		}
		return text;
	}

	private static void WriteLocal(string category, string msg, string memberName, string filePath, int lineNumber)
	{
		try
		{
			string value = SanitizeMessage(msg);
			string contents = $"[{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] [{SourceFor(filePath, lineNumber)}] {value}{Environment.NewLine}";
			string path = Path.Combine(LogDir, "kuro-" + category + ".log");
			lock (LocalLogLock)
			{
				Directory.CreateDirectory(LogDir);
				RotateIfNeeded(path);
				File.AppendAllText(path, contents);
			}
		}
		catch
		{
			if (Interlocked.Exchange(ref _localWriteFailureSignaled, 1) == 0)
			{
				Count.LocalLogWriteFailed(1L);
			}
		}
	}

	private static void RotateIfNeeded(string path)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(path);
			if (fileInfo.Exists && fileInfo.Length >= 5242880)
			{
				string text = path + ".1";
				if (File.Exists(text))
				{
					File.Delete(text);
				}
				File.Move(path, text);
			}
		}
		catch
		{
		}
	}

	internal static string SanitizeMessage(string message)
	{
		if (message.Length > 4096)
		{
			return message.Substring(0, 4096) + "...";
		}
		return message;
	}

	internal static string ReadTail(DateTimeOffset from, DateTimeOffset until, int budgetChars)
	{
		List<(DateTimeOffset, string)> entries = new List<(DateTimeOffset, string)>();
		try
		{
			foreach (string item in Directory.EnumerateFiles(LogDir, "kuro-*.log").Concat(Directory.EnumerateFiles(LogDir, "kuro-*.log.1")))
			{
				ParseEntries(ReadTailText(item), from, until, entries);
			}
		}
		catch
		{
		}
		return SelectTail(entries, budgetChars);
	}

	private static string ReadTailText(string path)
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		bool flag = fileStream.Length > 32768;
		if (flag)
		{
			fileStream.Seek(-32768L, SeekOrigin.End);
		}
		using StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8);
		string text = streamReader.ReadToEnd();
		if (!flag)
		{
			return text;
		}
		int num = text.IndexOf('\n');
		return (num < 0) ? "" : text.Substring(num + 1);
	}

	internal static void ParseEntries(string text, DateTimeOffset from, DateTimeOffset until, List<(DateTimeOffset At, string Text)> entries)
	{
		DateTimeOffset? at = null;
		StringBuilder current = null;
		string[] array = text.Split('\n');
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].TrimEnd('\r');
			if (TryParseStamp(text2, out var stamp))
			{
				Flush();
				at = stamp;
				current = new StringBuilder(text2);
			}
			else if (current != null)
			{
				string text3 = text2.Trim();
				if (text3 != null && text3.Length > 0)
				{
					current.Append(" | ").Append(text3);
				}
			}
		}
		Flush();
		void Flush()
		{
			if (at.HasValue)
			{
				DateTimeOffset valueOrDefault = at.GetValueOrDefault();
				if (current != null && valueOrDefault >= from && valueOrDefault < until)
				{
					string text4 = current.ToString();
					entries.Add((valueOrDefault, (text4.Length <= 512) ? text4 : (text4.Substring(0, 512) + "...")));
				}
			}
		}
	}

	private static bool TryParseStamp(string line, out DateTimeOffset stamp)
	{
		stamp = default;
		if (line.Length > 31 && line[0] == '[' && line[31] == ']')
		{
			return DateTimeOffset.TryParseExact(line.AsSpan(1, 30), "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
		}
		return false;
	}

	internal static string SelectTail(List<(DateTimeOffset At, string Text)> entries, int budgetChars)
	{
		List<(DateTimeOffset, string)> list = entries.OrderBy(((DateTimeOffset At, string Text) e) => e.At).ToList();
		int num = list.Count;
		int num2 = 0;
		while (num > 0 && num2 + list[num - 1].Item2.Length + 1 <= budgetChars)
		{
			num2 += list[num - 1].Item2.Length + 1;
			num--;
		}
		return string.Join('\n', list.Skip(num).Select(((DateTimeOffset At, string Text) e) => e.Text));
	}
}
