using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace KuroWindows.Storage;

public class StorageManager
{
	public readonly record struct InterruptedSessionInfo(string SessionDir, int SegmentIndex, DateTimeOffset UpdatedAtUtc);

	private sealed class ActiveSessionMarker
	{
		public int SegmentIndex { get; set; }

		public DateTimeOffset UpdatedAtUtc { get; set; }
	}

	private const string ActiveSessionMarkerFileName = ".active-session.json";

	private static readonly string RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, ".data");

	private static int _diskProbeFailureLogged;

	public string RecordingsDirectory()
	{
		if (!Directory.Exists(RootPath))
		{
			Directory.CreateDirectory(RootPath);
			string? directoryName = Path.GetDirectoryName(RootPath);
			File.SetAttributes(directoryName, File.GetAttributes(directoryName) | FileAttributes.Hidden);
		}
		return RootPath;
	}

	public string CreateSessionDirectory()
	{
		for (int i = 0; i < 3; i++)
		{
			string path = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
			string text = Path.Combine(RecordingsDirectory(), path);
			if (!Directory.Exists(text))
			{
				Directory.CreateDirectory(text);
				return text;
			}
			if (i < 2)
			{
				Thread.Sleep(1100 - DateTime.Now.Millisecond);
			}
		}
		throw new IOException("Session directory name collided repeatedly (clock step?).");
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

	public long AvailableDiskSpaceBytes()
	{
		string text = RootPath;
		while (!string.IsNullOrEmpty(text) && !Directory.Exists(text))
		{
			text = Path.GetDirectoryName(text);
		}
		if (!string.IsNullOrEmpty(text) && GetDiskFreeSpaceExW(text, out var lpFreeBytesAvailableToCaller, out var _, out var _))
		{
			if (lpFreeBytesAvailableToCaller <= long.MaxValue)
			{
				return (long)lpFreeBytesAvailableToCaller;
			}
			return long.MaxValue;
		}
		try
		{
			return new DriveInfo(Path.GetPathRoot(RootPath)).AvailableFreeSpace;
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _diskProbeFailureLogged, 1) == 0)
			{
				KuroLog.SystemEvent($"disk free-space probe failed drive={Path.GetPathRoot(RootPath)} type={ex.GetType().Name}: {ex.Message}; treating as unlimited", "AvailableDiskSpaceBytes", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\StorageManager.cs", 94);
			}
			return long.MaxValue;
		}
	}

	public long TotalDiskUsage()
	{
		string path = RecordingsDirectory();
		if (!Directory.Exists(path))
		{
			return 0L;
		}
		return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum((string f) => new FileInfo(f).Length);
	}

	public IEnumerable<string> EnumerateSessionDirectories()
	{
		return Directory.EnumerateDirectories(RecordingsDirectory());
	}

	public void MarkSessionActive(string sessionDir, int segmentIndex)
	{
		ActiveSessionMarker value = new ActiveSessionMarker
		{
			SegmentIndex = segmentIndex,
			UpdatedAtUtc = DateTimeOffset.UtcNow
		};
		File.WriteAllText(Path.Combine(sessionDir, ".active-session.json"), JsonSerializer.Serialize(value));
	}

	public void ClearSessionActive(string sessionDir)
	{
		string path = Path.Combine(sessionDir, ".active-session.json");
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	public static InterruptedSessionInfo? TryReadActiveSessionMarker(string sessionDir)
	{
		string path = Path.Combine(sessionDir, ".active-session.json");
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			ActiveSessionMarker activeSessionMarker = JsonSerializer.Deserialize<ActiveSessionMarker>(File.ReadAllText(path));
			return (activeSessionMarker == null) ? ((InterruptedSessionInfo?)null) : new InterruptedSessionInfo?(new InterruptedSessionInfo(sessionDir, activeSessionMarker.SegmentIndex, activeSessionMarker.UpdatedAtUtc));
		}
		catch (Exception ex)
		{
			KuroLog.Recording("StorageManager: invalid active session marker session=" + Path.GetFileName(sessionDir) + " error=" + ex.Message, "TryReadActiveSessionMarker", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\StorageManager.cs", 160);
			return null;
		}
	}
}
