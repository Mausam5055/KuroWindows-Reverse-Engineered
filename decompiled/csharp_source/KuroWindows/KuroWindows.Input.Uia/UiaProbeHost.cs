using System;
using System.Collections.Generic;
using System.Threading;
using ClientTelemetry;

namespace KuroWindows.Input.Uia;

internal sealed class UiaProbeHost
{
	private const int MinExchangeBudgetMs = 50;

	private readonly Func<int, (IUiaProbeTransport? Transport, string Failure)> _startTransport;

	private readonly Func<long> _nowMs;

	private readonly Action<Action> _runOffThread;

	private readonly object _sync = new object();

	private readonly SemaphoreSlim _exchangeGate = new SemaphoreSlim(1, 1);

	private readonly ManualResetEventSlim _spawnSettled = new ManualResetEventSlim(initialState: false);

	private readonly Dictionary<string, List<long>> _deathsByExe = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

	private IUiaProbeTransport? _transport;

	private bool _spawning;

	private long _nextSpawnAtMs;

	private int _consecutiveFailures;

	private int _spawnFailures;

	private int _seq;

	private bool _enabled = true;

	private bool _stopped;

	private string? _standDownReason;

	internal static UiaProbeHost Shared { get; } = new UiaProbeHost();

	internal UiaProbeHost()
		: this(UiaProbeProcessTransport.TryStart, () => Environment.TickCount64, RunOffThread)
	{
	}

	internal UiaProbeHost(Func<int, (IUiaProbeTransport? Transport, string Failure)> startTransport, Func<long> nowMs, Action<Action> runOffThread)
	{
		_startTransport = startTransport;
		_nowMs = nowMs;
		_runOffThread = runOffThread;
	}

	private static void RunOffThread(Action action)
	{
		Thread thread = new Thread(() =>
		{
			action();
		});
		thread.IsBackground = true;
		thread.Name = "KuroUiaProbeWork";
		thread.Start();
	}

	internal void Warm()
	{
		if (TryClaimSpawn())
		{
			_runOffThread(SpawnOnce);
		}
	}

	internal void SetEnabled(bool enabled)
	{
		IUiaProbeTransport victim = null;
		lock (_sync)
		{
			if (_enabled == enabled)
			{
				return;
			}
			_enabled = enabled;
			if (enabled)
			{
				_nextSpawnAtMs = 0L;
				_consecutiveFailures = 0;
				_spawnFailures = 0;
				_standDownReason = null;
			}
			else
			{
				victim = _transport;
				_transport = null;
				_spawnSettled.Reset();
			}
		}
		if (victim != null)
		{
			_runOffThread(() =>
			{
				Retire(victim);
			});
		}
		KuroLog.SystemEvent(enabled ? "uia probe enabled by server flag" : "uia probe disabled by server flag; password-field detection falls back to title/app policy", "SetEnabled", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 221);
	}

	internal void Shutdown(string reason)
	{
		IUiaProbeTransport transport;
		lock (_sync)
		{
			if (_stopped)
			{
				return;
			}
			_stopped = true;
			transport = _transport;
			_transport = null;
			_spawnSettled.Reset();
		}
		Retire(transport);
		KuroLog.SystemEvent("uia probe helper stopped reason=" + reason, "Shutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 249);
	}

	internal UiaProbeResult Probe(UiaProbeSite site, UiaProbeReads reads, string targetExe, string targetClass, int targetPid, long targetWindow, int deadlineMs)
	{
		if (reads == UiaProbeReads.None)
		{
			return UiaProbeResult.Unavailable;
		}
		long num = _nowMs();
		IUiaProbeTransport transport;
		bool flag;
		bool flag2;
		lock (_sync)
		{
			if (_stopped)
			{
				return UiaProbeResult.Unavailable;
			}
			if (!_enabled || _standDownReason != null)
			{
				return UiaProbeResult.Unavailable;
			}
			transport = _transport;
			flag = transport == null && TryClaimSpawnLocked();
			flag2 = flag || _spawning;
		}
		if (flag)
		{
			_runOffThread(SpawnOnce);
		}
		if (transport == null)
		{
			if (!flag2)
			{
				return UiaProbeResult.Unavailable;
			}
			if (!_spawnSettled.Wait(Math.Max(0, deadlineMs)))
			{
				return UiaProbeResult.Unavailable;
			}
			lock (_sync)
			{
				transport = _transport;
			}
			if (transport == null)
			{
				return UiaProbeResult.Unavailable;
			}
		}
		int num2 = deadlineMs - (int)(_nowMs() - num);
		if (num2 < 50)
		{
			return UiaProbeResult.Unavailable;
		}
		if (!_exchangeGate.Wait(num2))
		{
			return UiaProbeResult.Unavailable;
		}
		try
		{
			lock (_sync)
			{
				if (_transport != transport)
				{
					return UiaProbeResult.Unavailable;
				}
			}
			num2 = deadlineMs - (int)(_nowMs() - num);
			if (num2 < 50)
			{
				return UiaProbeResult.Unavailable;
			}
			UiaProbeRequest request = new UiaProbeRequest(Interlocked.Increment(ref _seq), site, reads, targetExe, targetClass, targetPid, targetWindow);
			if (transport.TryExchange(in request, num2, out UiaProbeResponse response, out string failure))
			{
				lock (_sync)
				{
					_consecutiveFailures = 0;
				}
				return new UiaProbeResult(response.Status, response.IsSecure, response.SelectedText);
			}
			HandleChannelFailure(transport, in request, failure);
			return UiaProbeResult.Unavailable;
		}
		finally
		{
			_exchangeGate.Release();
		}
	}

	private void HandleChannelFailure(IUiaProbeTransport transport, in UiaProbeRequest request, string failure)
	{
		int num;
		long num2;
		lock (_sync)
		{
			if (_transport != transport)
			{
				return;
			}
			_transport = null;
			_spawnSettled.Reset();
			num = ++_consecutiveFailures;
			num2 = BackoffMsFor(num);
			_nextSpawnAtMs = _nowMs() + num2;
		}
		int pid = transport.Pid;
		int? exitCode = transport.Terminate();
		transport.Dispose();
		Count.UiaProbe(UiaProbeEvent.Lost, 1L);
		KuroLog.SystemEvent($"uia probe helper lost site={request.Site} reason={failure} pid={pid} exit_code={FormatExitCode(exitCode)} target_exe={Safe(request.TargetExe)} target_class={Safe(request.TargetClass)} target_pid={request.TargetPid} consecutive={num} backoff_ms={num2}", "HandleChannelFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 413);
		if (RecordDeath(request.TargetExe))
		{
			Count.UiaProbe(UiaProbeEvent.AppSuppressed, 1L);
			KuroLog.SystemEvent($"uia probe app suppressed exe={Safe(request.TargetExe)} deaths={AppConfig.UiaProbeAppDeathThreshold} window_minutes={AppConfig.UiaProbeAppDeathWindowMinutes} suppressed_minutes={AppConfig.UiaProbeAppSuppressionMinutes}; " + "skipping UI Automation for this app until that expires", "HandleChannelFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 422);
		}
	}

	private void SpawnOnce()
	{
		IUiaProbeTransport uiaProbeTransport;
		string text;
		try
		{
			(uiaProbeTransport, text) = _startTransport(AppConfig.UiaProbeConnectTimeoutMs);
		}
		catch (Exception ex)
		{
			uiaProbeTransport = null;
			text = "start_threw:" + ex.GetType().Name;
		}
		if (uiaProbeTransport != null)
		{
			bool flag = false;
			lock (_sync)
			{
				_spawning = false;
				if (_stopped || !_enabled || _standDownReason != null)
				{
					flag = true;
				}
				else
				{
					_transport = uiaProbeTransport;
					_spawnFailures = 0;
				}
			}
			_spawnSettled.Set();
			if (flag)
			{
				Retire(uiaProbeTransport);
				return;
			}
			Count.UiaProbe(UiaProbeEvent.Ready, 1L);
			KuroLog.SystemEvent($"uia probe helper ready pid={uiaProbeTransport.Pid}", "SpawnOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 473);
			return;
		}
		string text2 = null;
		int num;
		long num2;
		lock (_sync)
		{
			_spawning = false;
			num = ++_spawnFailures;
			num2 = BackoffMsFor(num);
			_nextSpawnAtMs = _nowMs() + num2;
			if (num >= AppConfig.UiaProbeSpawnFailureLimit && _standDownReason == null)
			{
				_standDownReason = text;
				text2 = text;
			}
		}
		_spawnSettled.Set();
		Count.UiaProbe(UiaProbeEvent.SpawnFailed, 1L);
		KuroLog.SystemEvent($"uia probe helper spawn failed reason={text} attempts={num} backoff_ms={num2}", "SpawnOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 495);
		if (text2 != null)
		{
			Count.UiaProbe(UiaProbeEvent.StoodDown, 1L);
			Telemetry.Emit(Ev.UiaStoodDown(text2));
			KuroLog.SystemEvent("uia probe stood down reason=" + text2 + "; password-field detection falls back to title/app policy and text selection is off", "SpawnOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeHost.cs", 506);
		}
	}

	private bool TryClaimSpawn()
	{
		lock (_sync)
		{
			return TryClaimSpawnLocked();
		}
	}

	private bool TryClaimSpawnLocked()
	{
		if (_spawning || _transport != null || _stopped || !_enabled || _standDownReason != null)
		{
			return false;
		}
		if (_nowMs() < _nextSpawnAtMs)
		{
			return false;
		}
		_spawnSettled.Reset();
		_spawning = true;
		return true;
	}

	private bool RecordDeath(string exeName)
	{
		if (string.IsNullOrEmpty(exeName))
		{
			return false;
		}
		long now = _nowMs();
		long windowMs = (long)AppConfig.UiaProbeAppDeathWindowMinutes * 60000L;
		lock (_sync)
		{
			if (!_deathsByExe.TryGetValue(exeName, out List<long> value))
			{
				value = new List<long>();
				_deathsByExe[exeName] = value;
			}
			value.RemoveAll((long stamp) => now - stamp > windowMs);
			value.Add(now);
			if (value.Count < AppConfig.UiaProbeAppDeathThreshold)
			{
				return false;
			}
		}
		return UiaProbeSuppression.Suppress(exeName, (long)AppConfig.UiaProbeAppSuppressionMinutes * 60000L);
	}

	private static long BackoffMsFor(int attempt)
	{
		int[] uiaProbeRespawnBackoffSeconds = AppConfig.UiaProbeRespawnBackoffSeconds;
		int num = Math.Clamp(attempt - 1, 0, uiaProbeRespawnBackoffSeconds.Length - 1);
		return (long)uiaProbeRespawnBackoffSeconds[num] * 1000L;
	}

	private static void Retire(IUiaProbeTransport? transport)
	{
		if (transport != null)
		{
			transport.Terminate();
			transport.Dispose();
		}
	}

	private static string FormatExitCode(int? exitCode)
	{
		if (!exitCode.HasValue)
		{
			return "-";
		}
		return $"0x{exitCode.Value:x8}";
	}

	private static string Safe(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		return "-";
	}

	internal static void ResetSuppressionForTests()
	{
		UiaProbeSuppression.ResetForTests();
	}
}
