using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace KuroWindows.Upload;

internal sealed class UploadRetryScheduler : IDisposable
{
	private readonly Func<bool> _canTrigger;

	private readonly Action _trigger;

	private readonly ConcurrentDictionary<string, byte> _scheduled = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _scheduleLogged = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly object _lifecycleLock = new object();

	private CancellationTokenSource? _cts;

	public UploadRetryScheduler(Func<bool> canTrigger, Action trigger)
	{
		_canTrigger = canTrigger;
		_trigger = trigger;
	}

	public void Start()
	{
		lock (_lifecycleLock)
		{
			if (_cts == null)
			{
				_cts = new CancellationTokenSource();
			}
		}
	}

	public void Stop()
	{
		CancellationTokenSource cts;
		lock (_lifecycleLock)
		{
			cts = _cts;
			_cts = null;
		}
		try
		{
			cts?.Cancel();
		}
		catch
		{
		}
		cts?.Dispose();
		_scheduled.Clear();
		_scheduleLogged.Clear();
	}

	public void Forget(string key)
	{
		_scheduled.TryRemove(key, out var value);
		_scheduleLogged.TryRemove(key, out value);
	}

	public void Schedule(string key, TimeSpan delay, string logPrefix)
	{
		CancellationTokenSource cts;
		lock (_lifecycleLock)
		{
			cts = _cts;
		}
		if (cts == null || !_scheduled.TryAdd(key, 0))
		{
			return;
		}
		CancellationToken token = cts.Token;
		Task.Run(async () =>
		{
			try
			{
				if (_scheduleLogged.TryAdd(key, 0))
				{
					Log($"{logPrefix} scheduled in {delay.TotalSeconds:F0}s", "Schedule", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadRetryScheduler.cs", 75);
				}
				await Task.Delay(delay, token).ConfigureAwait(continueOnCapturedContext: false);
				if (!token.IsCancellationRequested && _canTrigger())
				{
					_trigger();
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex2)
			{
				Log("Retry scheduler error for " + key + ": " + ex2.Message, "Schedule", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadRetryScheduler.cs", 88);
			}
			finally
			{
				_scheduled.TryRemove(key, out var _);
			}
		});
	}

	private static void Log(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Upload(msg, memberName, filePath, lineNumber);
	}

	public void Dispose()
	{
		Stop();
	}
}
