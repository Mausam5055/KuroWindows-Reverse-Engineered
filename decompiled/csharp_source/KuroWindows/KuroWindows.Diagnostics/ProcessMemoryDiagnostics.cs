using System;
using System.Diagnostics;
using System.Threading;
using ClientTelemetry;

namespace KuroWindows.Diagnostics;

internal static class ProcessMemoryDiagnostics
{
	private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30.0);

	private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(15.0);

	internal const long LogStepMb = 64L;

	private static readonly object Sync = new object();

	private static Timer? _timer;

	private static int _isSampling;

	private static long _baselinePrivateBytes;

	private static long _lastLoggedPrivateMb = long.MinValue;

	private static int _failureLogged;

	public static void Start()
	{
		lock (Sync)
		{
			if (_timer == null)
			{
				_timer = new Timer((object? _) =>
				{
					LogSnapshot();
				}, null, InitialDelay, SampleInterval);
			}
		}
	}

	public static void Stop()
	{
		lock (Sync)
		{
			_timer?.Dispose();
			_timer = null;
			_baselinePrivateBytes = 0L;
			_lastLoggedPrivateMb = long.MinValue;
		}
	}

	private static void LogSnapshot()
	{
		if (Interlocked.Exchange(ref _isSampling, 1) != 0)
		{
			return;
		}
		try
		{
			using Process process = Process.GetCurrentProcess();
			process.Refresh();
			long privateMemorySize = process.PrivateMemorySize64;
			if (Interlocked.Read(in _baselinePrivateBytes) <= 0)
			{
				Interlocked.CompareExchange(ref _baselinePrivateBytes, privateMemorySize, 0L);
			}
			long num = Math.Max(Interlocked.Read(in _baselinePrivateBytes), 1L);
			long num2 = ToMb(privateMemorySize);
			Observe.MemoryPrivateMb(num2);
			long num3 = Interlocked.Read(in _lastLoggedPrivateMb);
			if (num3 == long.MinValue || Math.Abs(num2 - num3) >= 64)
			{
				Interlocked.Exchange(ref _lastLoggedPrivateMb, num2);
				long value = ToMb(privateMemorySize - num);
				GCMemoryInfo gCMemoryInfo = GC.GetGCMemoryInfo();
				KuroLog.SystemEvent("Process memory snapshot " + $"private_mb={num2} " + $"private_delta_mb={value} " + $"working_set_mb={ToMb(process.WorkingSet64)} " + $"managed_heap_mb={ToMb(GC.GetTotalMemory(forceFullCollection: false))} " + $"gc_heap_mb={ToMb(gCMemoryInfo.HeapSizeBytes)} " + $"committed_gc_mb={ToMb(gCMemoryInfo.TotalCommittedBytes)} " + $"handles={process.HandleCount} " + $"threads={process.Threads.Count} " + $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}", "LogSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\ProcessMemoryDiagnostics.cs", 81);
			}
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
			{
				KuroLog.SystemEvent("Process memory snapshot failed: " + ex.Message, "LogSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\ProcessMemoryDiagnostics.cs", 97);
			}
		}
		finally
		{
			Interlocked.Exchange(ref _isSampling, 0);
		}
	}

	private static long ToMb(long bytes)
	{
		if (bytes < 0)
		{
			return -((-bytes + 1048576 - 1) / 1048576);
		}
		return (bytes + 1048576 - 1) / 1048576;
	}
}
