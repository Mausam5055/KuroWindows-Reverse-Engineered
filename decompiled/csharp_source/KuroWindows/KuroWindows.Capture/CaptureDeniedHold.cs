using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KuroWindows.Capture;

internal sealed class CaptureDeniedHold
{
	public const int PollDelayMs = 2000;

	public const int DefaultCapSeconds = 45;

	public const int MaxCapSeconds = 300;

	internal const string EscalationMessage = "Windows still denies DXGI desktop-image access after retry.";

	private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(2000.0);

	private readonly int _displayIndex;

	private readonly Action _markSourceUnavailable;

	private readonly Action<int> _signalCaptureAccessDenied;

	private readonly Action _tryRecreateDuplicationOnly;

	private readonly Func<long> _qpcNow;

	private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

	public int CapSeconds { get; }

	public CaptureDeniedHold(int displayIndex, int? configuredCapSeconds, Action markSourceUnavailable, Action<int> signalCaptureAccessDenied, Action tryRecreateDuplicationOnly)
		: this(displayIndex, configuredCapSeconds, markSourceUnavailable, signalCaptureAccessDenied, tryRecreateDuplicationOnly, Stopwatch.GetTimestamp, Task.Delay)
	{
	}

	internal CaptureDeniedHold(int displayIndex, int? configuredCapSeconds, Action markSourceUnavailable, Action<int> signalCaptureAccessDenied, Action tryRecreateDuplicationOnly, Func<long> qpcNow, Func<TimeSpan, CancellationToken, Task> delayAsync)
	{
		_displayIndex = displayIndex;
		CapSeconds = EffectiveCapSeconds(configuredCapSeconds);
		_markSourceUnavailable = markSourceUnavailable;
		_signalCaptureAccessDenied = signalCaptureAccessDenied;
		_tryRecreateDuplicationOnly = tryRecreateDuplicationOnly;
		_qpcNow = qpcNow;
		_delayAsync = delayAsync;
	}

	public static int EffectiveCapSeconds(int? configuredSeconds)
	{
		return Math.Clamp(configuredSeconds ?? 45, 0, 300);
	}

	public async Task<CaptureDeniedHoldResult> RunAsync(int entryHResult, CancellationToken cancellationToken)
	{
		_markSourceUnavailable();
		_signalCaptureAccessDenied(entryHResult);
		KuroLog.Capture($"capture_denied_hold state=enter display={_displayIndex} hr=0x{entryHResult:X8} remote_session={RemoteSessionProbe.LogValue}", "RunAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureDeniedHold.cs", 173);
		long enterQpc = _qpcNow();
		int polls = 0;
		while (true)
		{
			try
			{
				await _delayAsync(PollDelay, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				LogCancelled(enterQpc, polls);
				return CaptureDeniedHoldResult.Cancelled();
			}
			polls++;
			try
			{
				_tryRecreateDuplicationOnly();
			}
			catch (OperationCanceledException)
			{
				LogCancelled(enterQpc, polls);
				return CaptureDeniedHoldResult.Cancelled();
			}
			catch (Exception ex3) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex3))
			{
				long num = HeldMs(enterQpc);
				if (num >= (long)CapSeconds * 1000L)
				{
					KuroLog.Capture($"capture_denied_hold state=exit outcome=escalated held_ms={num} polls={polls} display={_displayIndex}", "RunAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureDeniedHold.cs", 207);
					return CaptureDeniedHoldResult.Escalated(new CaptureAccessDeniedException("Windows still denies DXGI desktop-image access after retry.", ex3));
				}
				continue;
			}
			catch (Exception ex4)
			{
				KuroLog.Capture($"capture_denied_hold state=exit outcome=device_error held_ms={HeldMs(enterQpc)} polls={polls} hr=0x{ex4.HResult:X8} display={_displayIndex}", "RunAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureDeniedHold.cs", 220);
				throw;
			}
			break;
		}
		KuroLog.Capture($"capture_denied_hold state=exit outcome=recovered held_ms={HeldMs(enterQpc)} polls={polls} display={_displayIndex}", "RunAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureDeniedHold.cs", 226);
		return CaptureDeniedHoldResult.Recovered();
	}

	private long HeldMs(long enterQpc)
	{
		return (long)((double)(_qpcNow() - enterQpc) * 1000.0 / (double)Stopwatch.Frequency);
	}

	private void LogCancelled(long enterQpc, int polls)
	{
		KuroLog.LocalOnly("capture", $"capture_denied_hold state=exit outcome=cancelled held_ms={HeldMs(enterQpc)} polls={polls} display={_displayIndex}", "LogCancelled", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureDeniedHold.cs", 237);
	}
}
