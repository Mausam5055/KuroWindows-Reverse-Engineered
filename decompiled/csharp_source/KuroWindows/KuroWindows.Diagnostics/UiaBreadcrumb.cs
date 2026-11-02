using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace KuroWindows.Diagnostics;

internal static class UiaBreadcrumb
{
	public sealed record InFlightRecord(string Op, string TargetExe, string TargetClass, int TargetPid, long SeqStarted, long SeqDone, DateTimeOffset StartedAt);

	private const int RecordBytes = 256;

	private const int SeqStartedOffset = 0;

	private const int SeqDoneOffset = 8;

	private const int StartedAtOffset = 16;

	private const int PidOffset = 24;

	private const int OpOffset = 28;

	private const int OpMaxBytes = 40;

	private const int ExeOffset = 69;

	private const int ExeMaxBytes = 80;

	private const int ClassOffset = 150;

	private const int ClassMaxBytes = 80;

	private static readonly object Sync = new object();

	private static string _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "diagnostics", "uia-breadcrumb.bin");

	private static MemoryMappedFile? _mmf;

	private static MemoryMappedViewAccessor? _view;

	private static bool _failed;

	internal static void OverridePathForTests(string path)
	{
		lock (Sync)
		{
			DisposeWriter();
			_failed = false;
			_filePath = path;
		}
	}

	public static void MarkStart(string op, string targetExe, string targetClass, int targetPid)
	{
		lock (Sync)
		{
			MemoryMappedViewAccessor memoryMappedViewAccessor = EnsureView();
			if (memoryMappedViewAccessor == null)
			{
				return;
			}
			try
			{
				long num = memoryMappedViewAccessor.ReadInt64(0L) + 1;
				memoryMappedViewAccessor.Write(16L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
				memoryMappedViewAccessor.Write(24L, targetPid);
				WriteString(memoryMappedViewAccessor, 28, 40, op);
				WriteString(memoryMappedViewAccessor, 69, 80, targetExe);
				WriteString(memoryMappedViewAccessor, 150, 80, targetClass);
				memoryMappedViewAccessor.Write(8L, num - 1);
				memoryMappedViewAccessor.Write(0L, num);
			}
			catch
			{
				DisableAfterFailure();
			}
		}
	}

	public static void MarkDone()
	{
		lock (Sync)
		{
			MemoryMappedViewAccessor view = _view;
			if (view == null || _failed)
			{
				return;
			}
			try
			{
				view.Write(8L, view.ReadInt64(0L));
			}
			catch
			{
				DisableAfterFailure();
			}
		}
	}

	public static InFlightRecord? TryReadInFlight()
	{
		try
		{
			if (!File.Exists(_filePath))
			{
				return null;
			}
			using FileStream fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			byte[] array = new byte[256];
			if (fileStream.Read(array, 0, 256) < 256)
			{
				return null;
			}
			long num = BitConverter.ToInt64(array, 0);
			long num2 = BitConverter.ToInt64(array, 8);
			if (num <= 0 || num2 >= num)
			{
				return null;
			}
			return new InFlightRecord(ReadString(array, 28, 40), ReadString(array, 69, 80), ReadString(array, 150, 80), BitConverter.ToInt32(array, 24), num, num2, DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(BitConverter.ToInt64(array, 16), 0L, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())));
		}
		catch
		{
			return null;
		}
	}

	public static void Clear()
	{
		lock (Sync)
		{
			DisposeWriter();
			try
			{
				File.Delete(_filePath);
			}
			catch
			{
			}
		}
	}

	private static MemoryMappedViewAccessor? EnsureView()
	{
		if (_failed)
		{
			return null;
		}
		if (_view != null)
		{
			return _view;
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
			_mmf = MemoryMappedFile.CreateFromFile(new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete), null, 256L, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
			_view = _mmf.CreateViewAccessor(0L, 256L, MemoryMappedFileAccess.ReadWrite);
			return _view;
		}
		catch
		{
			DisableAfterFailure();
			return null;
		}
	}

	private static void DisableAfterFailure()
	{
		_failed = true;
		DisposeWriter();
	}

	private static void DisposeWriter()
	{
		try
		{
			_view?.Dispose();
		}
		catch
		{
		}
		_view = null;
		try
		{
			_mmf?.Dispose();
		}
		catch
		{
		}
		_mmf = null;
	}

	private static void WriteString(MemoryMappedViewAccessor view, int offset, int maxBytes, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
		int num = Math.Min(bytes.Length, maxBytes);
		view.Write(offset, (byte)num);
		if (num > 0)
		{
			view.WriteArray(offset + 1, bytes, 0, num);
		}
	}

	private static string ReadString(byte[] buffer, int offset, int maxBytes)
	{
		int num = buffer[offset];
		if (num <= 0 || num > maxBytes)
		{
			return "";
		}
		return Encoding.UTF8.GetString(buffer, offset + 1, num);
	}
}
