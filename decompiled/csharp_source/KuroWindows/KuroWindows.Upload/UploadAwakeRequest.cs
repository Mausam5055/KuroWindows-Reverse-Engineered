using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KuroWindows.Upload;

internal sealed class UploadAwakeRequest
{
	private readonly record struct PendingLog(string Message, bool LocalOnly);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ReasonContext
	{
		public uint Version;

		public uint Flags;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string SimpleReasonString;
	}

	private const int PowerRequestSystemRequired = 1;

	private const uint PowerRequestContextVersion = 0u;

	private const uint PowerRequestContextSimpleString = 1u;

	private readonly object _sync = new object();

	private SafeFileHandle? _handle;

	private bool _active;

	private bool _unavailableLogged;

	private string? _unavailableMessage;

	public void Acquire(string detail)
	{
		PendingLog? log = null;
		lock (_sync)
		{
			if (_active)
			{
				return;
			}
			SafeFileHandle safeFileHandle = EnsureHandle();
			if (safeFileHandle == null)
			{
				log = TakeUnavailableLog();
			}
			else if (!PowerSetRequest(safeFileHandle, 1))
			{
				SetUnavailable($"PowerSetRequest failed win32={Marshal.GetLastWin32Error()}");
				log = TakeUnavailableLog();
			}
			else
			{
				_active = true;
				log = new PendingLog("Upload awake request acquired detail=" + detail, LocalOnly: false);
			}
		}
		Log(log);
	}

	public void Release(string reason)
	{
		PendingLog? log = null;
		lock (_sync)
		{
			if (!_active || _handle == null || _handle.IsInvalid)
			{
				return;
			}
			if (!PowerClearRequest(_handle, 1))
			{
				SetUnavailable($"PowerClearRequest failed win32={Marshal.GetLastWin32Error()}");
				log = TakeUnavailableLog();
			}
			else
			{
				_active = false;
				log = new PendingLog("Upload awake request released reason=" + reason, LocalOnly: false);
			}
		}
		Log(log);
	}

	private SafeFileHandle? EnsureHandle()
	{
		SafeFileHandle handle = _handle;
		if (handle != null && !handle.IsInvalid)
		{
			return _handle;
		}
		ReasonContext context = new ReasonContext
		{
			Version = 0u,
			Flags = 1u,
			SimpleReasonString = "Kuro is uploading queued recording segments"
		};
		SafeFileHandle safeFileHandle = PowerCreateRequest(ref context);
		if (safeFileHandle.IsInvalid)
		{
			SetUnavailable($"PowerCreateRequest failed win32={Marshal.GetLastWin32Error()}");
			safeFileHandle.Dispose();
			return null;
		}
		_handle = safeFileHandle;
		_unavailableLogged = false;
		return _handle;
	}

	private void SetUnavailable(string message)
	{
		_unavailableMessage = message;
	}

	private PendingLog? TakeUnavailableLog()
	{
		if (_unavailableMessage == null)
		{
			return null;
		}
		string message = "Upload awake request unavailable: " + _unavailableMessage;
		_unavailableMessage = null;
		if (_unavailableLogged)
		{
			return new PendingLog(message, LocalOnly: true);
		}
		_unavailableLogged = true;
		return new PendingLog(message, LocalOnly: false);
	}

	private static void Log(PendingLog? log)
	{
		if (log.HasValue)
		{
			if (log.Value.LocalOnly)
			{
				KuroLog.LocalOnly("upload", log.Value.Message, "Log", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadAwakeRequest.cs", 132);
			}
			else
			{
				KuroLog.Upload(log.Value.Message, "Log", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadAwakeRequest.cs", 136);
			}
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool PowerSetRequest(SafeFileHandle powerRequest, int requestType);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool PowerClearRequest(SafeFileHandle powerRequest, int requestType);
}
