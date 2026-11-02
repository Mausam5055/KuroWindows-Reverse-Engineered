using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KuroWindows.Capture;

internal static class RemoteSessionProbe
{
	private const int SmRemoteSession = 4096;

	private static readonly nint WtsCurrentServerHandle = IntPtr.Zero;

	private const int WtsCurrentSession = -1;

	private const int WtsConnectStateInfoClass = 14;

	private const int WtsDisconnected = 4;

	private static int _environmentEpoch;

	private static int _connectStateFailOpenSignaled;

	public static bool IsRemoteSession => GetSystemMetrics(4096) != 0;

	public static string LogValue
	{
		get
		{
			if (!IsRemoteSession)
			{
				return "false";
			}
			return "true";
		}
	}

	public static SessionConnectState ConnectState
	{
		get
		{
			nint buffer = IntPtr.Zero;
			try
			{
				if (!WTSQuerySessionInformation(WtsCurrentServerHandle, -1, 14, out buffer, out var bytesReturned) || bytesReturned < 4)
				{
					SignalConnectStateFailOpen($"query_failed bytes={bytesReturned} win32={Marshal.GetLastWin32Error()}");
					return SessionConnectState.Attached;
				}
				return ClassifyConnectState(Marshal.ReadInt32(buffer));
			}
			catch (Exception ex)
			{
				SignalConnectStateFailOpen(ex.GetType().Name + ": " + ex.Message);
				return SessionConnectState.Attached;
			}
			finally
			{
				if (buffer != IntPtr.Zero)
				{
					WTSFreeMemory(buffer);
				}
			}
		}
	}

	public static SessionEnvironmentKind Kind
	{
		get
		{
			if (IsRemoteSession)
			{
				if (ConnectState != SessionConnectState.Disconnected)
				{
					return SessionEnvironmentKind.RemoteAttached;
				}
				return SessionEnvironmentKind.RemoteDisconnected;
			}
			return SessionEnvironmentKind.Console;
		}
	}

	public static string SessionStateLogValue => Kind switch
	{
		SessionEnvironmentKind.Console => "console", 
		SessionEnvironmentKind.RemoteAttached => "remote_attached", 
		_ => "remote_disconnected", 
	};

	public static int EnvironmentEpoch => Volatile.Read(in _environmentEpoch);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytesReturned);

	[DllImport("wtsapi32.dll")]
	private static extern void WTSFreeMemory(nint buffer);

	internal static SessionConnectState ClassifyConnectState(int wtsConnectState)
	{
		if (wtsConnectState != 4)
		{
			return SessionConnectState.Attached;
		}
		return SessionConnectState.Disconnected;
	}

	public static int BumpEnvironmentEpoch()
	{
		return Interlocked.Increment(ref _environmentEpoch);
	}

	private static void SignalConnectStateFailOpen(string detail)
	{
		if (Interlocked.Exchange(ref _connectStateFailOpenSignaled, 1) == 0)
		{
			KuroLog.LocalOnly("capture", "session connect state query failed; treating as attached (fail-open) " + detail, "SignalConnectStateFailOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RemoteSessionProbe.cs", 165);
		}
	}

	internal static void ResetForTests()
	{
		Volatile.Write(ref _environmentEpoch, 0);
		Volatile.Write(ref _connectStateFailOpenSignaled, 0);
	}
}
