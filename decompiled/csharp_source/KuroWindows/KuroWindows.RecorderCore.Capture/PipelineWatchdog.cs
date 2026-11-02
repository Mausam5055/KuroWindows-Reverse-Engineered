using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KuroWindows.Capture;

namespace KuroWindows.RecorderCore.Capture;

internal sealed class PipelineWatchdog
{
	private enum LegOutcome
	{
		Quiet,
		Armed,
		SuspendGap,
		Confirmed
	}

	private struct PendingConfirmation
	{
		public long ArmedQpc;

		public long ArmedUnbiasedHns;
	}

	private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3.0);

	private const double SuspendDivergenceSeconds = 5.0;

	private const double ConfirmMinSpacingSeconds = 1.0;

	private readonly int _displayIndex;

	private readonly PipelineHeartbeat _heartbeat;

	private readonly Func<bool> _hasFirstFrame;

	private readonly Func<long> _gateOpenElapsedMsOrNegative;

	private readonly Func<int> _initialFrameTimeoutSeconds;

	private readonly Action<CapturePipelineStallException> _onStall;

	private readonly Func<bool>? _extraCheck;

	private readonly CancellationToken _cancellationToken;

	private readonly Func<bool>? _initialLegSuppressed;

	private readonly Action? _extraLegRearm;

	private readonly Func<bool>? _stallLegFireEnabled;

	private readonly Func<long> _qpcNow;

	private readonly Func<long> _unbiasedNowHns;

	private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

	private long _initialLegBaselineMs;

	private PendingConfirmation _initialPending;

	private PendingConfirmation _stallPending;

	private PendingConfirmation _extraPending;

	private bool _stallFireGateLogged;

	public Task Completion { get; }

	public PipelineWatchdog(int displayIndex, PipelineHeartbeat heartbeat, Func<bool> hasFirstFrame, Func<long> gateOpenElapsedMsOrNegative, Func<int> initialFrameTimeoutSeconds, Action<CapturePipelineStallException> onStall, Func<bool>? extraCheck, CancellationToken cancellationToken, Func<bool>? initialLegSuppressed = null, Action? extraLegRearm = null, Func<bool>? stallLegFireEnabled = null)
		: this(displayIndex, heartbeat, hasFirstFrame, gateOpenElapsedMsOrNegative, initialFrameTimeoutSeconds, onStall, extraCheck, cancellationToken, initialLegSuppressed, extraLegRearm, stallLegFireEnabled, Stopwatch.GetTimestamp, ReadUnbiasedInterruptTimeHns, Task.Delay)
	{
	}

	internal PipelineWatchdog(int displayIndex, PipelineHeartbeat heartbeat, Func<bool> hasFirstFrame, Func<long> gateOpenElapsedMsOrNegative, Func<int> initialFrameTimeoutSeconds, Action<CapturePipelineStallException> onStall, Func<bool>? extraCheck, CancellationToken cancellationToken, Func<bool>? initialLegSuppressed, Action? extraLegRearm, Func<bool>? stallLegFireEnabled, Func<long> qpcNow, Func<long> unbiasedNowHns, Func<TimeSpan, CancellationToken, Task> delayAsync)
	{
		_displayIndex = displayIndex;
		_heartbeat = heartbeat;
		_hasFirstFrame = hasFirstFrame;
		_gateOpenElapsedMsOrNegative = gateOpenElapsedMsOrNegative;
		_initialFrameTimeoutSeconds = initialFrameTimeoutSeconds;
		_onStall = onStall;
		_extraCheck = extraCheck;
		_cancellationToken = cancellationToken;
		_initialLegSuppressed = initialLegSuppressed;
		_extraLegRearm = extraLegRearm;
		_stallLegFireEnabled = stallLegFireEnabled;
		_qpcNow = qpcNow;
		_unbiasedNowHns = unbiasedNowHns;
		_delayAsync = delayAsync;
		Completion = Task.Run((Func<Task?>)RunAsync);
	}

	private async Task RunAsync()
	{
		long lastTickQpc = _qpcNow();
		long lastTickUnbiasedHns = _unbiasedNowHns();
		while (!_cancellationToken.IsCancellationRequested)
		{
			try
			{
				await _delayAsync(TickInterval, _cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			long num = _qpcNow();
			long num2 = _unbiasedNowHns();
			double num3 = (double)(num - lastTickQpc) / (double)Stopwatch.Frequency;
			double num4 = (double)(num2 - lastTickUnbiasedHns) / 10000000.0;
			lastTickQpc = num;
			lastTickUnbiasedHns = num2;
			if (Math.Abs(num3 - num4) > 5.0)
			{
				RearmAfterSuspend(num4, num3);
				continue;
			}
			bool stale = EvaluateInitialLegStale(out var gateOpenElapsedMs);
			double qpcDeltaSeconds;
			double unbiasedDeltaSeconds;
			switch (Confirm(ref _initialPending, stale, num, num2, out qpcDeltaSeconds, out unbiasedDeltaSeconds))
			{
			case LegOutcome.SuspendGap:
				RearmAfterSuspend(unbiasedDeltaSeconds, qpcDeltaSeconds);
				continue;
			case LegOutcome.Confirmed:
				FireInitialFrame(gateOpenElapsedMs);
				return;
			}
			double num5 = _heartbeat.SilenceSeconds(num);
			bool stale2 = _hasFirstFrame() && num5 > 25.0;
			switch (Confirm(ref _stallPending, stale2, num, num2, out qpcDeltaSeconds, out unbiasedDeltaSeconds))
			{
			case LegOutcome.SuspendGap:
				RearmAfterSuspend(unbiasedDeltaSeconds, qpcDeltaSeconds);
				continue;
			case LegOutcome.Confirmed:
			{
				Func<bool>? stallLegFireEnabled = _stallLegFireEnabled;
				if (stallLegFireEnabled != null && !stallLegFireEnabled())
				{
					_stallPending = default;
					if (!_stallFireGateLogged)
					{
						_stallFireGateLogged = true;
						KuroLog.LocalOnly("capture", $"stall watchdog stall-leg fire gated off by encoder_watchdog flag display={_displayIndex} silent_s={num5:F1}", "RunAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\PipelineWatchdog.cs", 207);
					}
					break;
				}
				FireStall(num5);
				return;
			}
			}
			if (_extraCheck != null)
			{
				bool stale3 = _hasFirstFrame() && _extraCheck();
				switch (Confirm(ref _extraPending, stale3, num, num2, out qpcDeltaSeconds, out unbiasedDeltaSeconds))
				{
				case LegOutcome.SuspendGap:
					RearmAfterSuspend(unbiasedDeltaSeconds, qpcDeltaSeconds);
					break;
				case LegOutcome.Confirmed:
					FireExtra(qpcDeltaSeconds);
					return;
				}
			}
		}
	}

	private bool EvaluateInitialLegStale(out long gateOpenElapsedMs)
	{
		gateOpenElapsedMs = _gateOpenElapsedMsOrNegative();
		if (gateOpenElapsedMs < 0)
		{
			_initialLegBaselineMs = 0L;
			return false;
		}
		Func<bool>? initialLegSuppressed = _initialLegSuppressed;
		if (initialLegSuppressed != null && initialLegSuppressed())
		{
			_initialLegBaselineMs = gateOpenElapsedMs;
			return false;
		}
		if (_hasFirstFrame())
		{
			return false;
		}
		if (gateOpenElapsedMs < _initialLegBaselineMs)
		{
			_initialLegBaselineMs = 0L;
		}
		return gateOpenElapsedMs - _initialLegBaselineMs > (long)_initialFrameTimeoutSeconds() * 1000L;
	}

	private void FireInitialFrame(long gateOpenElapsedMs)
	{
		int value = _initialFrameTimeoutSeconds();
		Fire(new CapturePipelineStallException(_displayIndex, (double)Math.Max(0L, gateOpenElapsedMs - _initialLegBaselineMs) / 1000.0, "initial_frame", $"capture produced no initial frames after {value}s gate_open_ms={gateOpenElapsedMs} display={_displayIndex}"));
	}

	private void FireStall(double silenceSeconds)
	{
		Fire(new CapturePipelineStallException(_displayIndex, silenceSeconds, "commit_wedge", $"encode loop produced no heartbeat for >{25}s (wedged native call) display={_displayIndex}"));
	}

	private void FireExtra(double confirmWindowSeconds)
	{
		Fire(new CapturePipelineStallException(_displayIndex, confirmWindowSeconds, "pool_starvation", $"capture pipeline extra stall check confirmed display={_displayIndex}"));
	}

	private void Fire(CapturePipelineStallException stall)
	{
		try
		{
			_onStall(stall);
		}
		catch
		{
		}
	}

	private void RearmAfterSuspend(double unbiasedDeltaSeconds, double qpcDeltaSeconds)
	{
		_heartbeat.Beat();
		_initialPending = default;
		_stallPending = default;
		_extraPending = default;
		_initialLegBaselineMs = Math.Max(0L, _gateOpenElapsedMsOrNegative());
		_extraLegRearm?.Invoke();
		KuroLog.LocalOnly("capture", $"stall watchdog re-armed after resume unbiased_delta_s={unbiasedDeltaSeconds:F1} qpc_delta_s={qpcDeltaSeconds:F1} display={_displayIndex}", "RearmAfterSuspend", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\PipelineWatchdog.cs", 317);
	}

	private static LegOutcome Confirm(ref PendingConfirmation pending, bool stale, long nowQpc, long nowUnbiasedHns, out double qpcDeltaSeconds, out double unbiasedDeltaSeconds)
	{
		qpcDeltaSeconds = 0.0;
		unbiasedDeltaSeconds = 0.0;
		if (!stale)
		{
			pending = default;
			return LegOutcome.Quiet;
		}
		if (pending.ArmedQpc == 0L)
		{
			pending.ArmedQpc = nowQpc;
			pending.ArmedUnbiasedHns = nowUnbiasedHns;
			return LegOutcome.Armed;
		}
		qpcDeltaSeconds = (double)(nowQpc - pending.ArmedQpc) / (double)Stopwatch.Frequency;
		if (qpcDeltaSeconds < 1.0)
		{
			return LegOutcome.Armed;
		}
		unbiasedDeltaSeconds = (double)(nowUnbiasedHns - pending.ArmedUnbiasedHns) / 10000000.0;
		if (Math.Abs(qpcDeltaSeconds - unbiasedDeltaSeconds) > 5.0)
		{
			return LegOutcome.SuspendGap;
		}
		return LegOutcome.Confirmed;
	}

	internal static long ReadUnbiasedInterruptTimeHns()
	{
		QueryUnbiasedInterruptTime(out var unbiasedInterruptTime);
		return unbiasedInterruptTime;
	}

	[DllImport("kernel32.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryUnbiasedInterruptTime(out long unbiasedInterruptTime);
}
