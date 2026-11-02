using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemAudio;
using KuroWindows.Capture.Gen2;
using KuroWindows.Input;
using KuroWindows.RecorderCore;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Encode;

namespace KuroWindows.Capture;

internal sealed class SessionCaptureEngine : IAsyncDisposable
{
	private readonly string _sessionDir;

	private readonly FrameStateSource? _frameState;

	private readonly RecorderWriteRouter _router;

	private readonly DisplayTopologyPolicyResult _displayPolicy;

	private readonly List<DisplayRecorder> _displays = new List<DisplayRecorder>();

	private readonly List<string> _startBanner = new List<string>();

	private int _captureFailureRaised;

	private int _captureAccessDeniedRaised;

	public Action<Exception>? CaptureFailed { get; set; }

	public Action? CaptureAccessDenied { get; set; }

	public Func<bool>? LegacyPadCadence { get; init; }

	public ISystemAudio? SystemAudio { get; init; }

	public string? DisplayPolicyLine { get; init; }

	public int DisplayCount => _displays.Count;

	public RecorderCoreKind RecorderCore { get; private set; }

	public SessionCaptureEngine(string sessionDir, FrameStateSource? frameState, RecorderWriteRouter router, DisplayTopologyPolicyResult displayPolicy)
	{
		_sessionDir = sessionDir;
		_frameState = frameState;
		_router = router;
		_displayPolicy = displayPolicy;
	}

	public void StartCapture()
	{
		if (!_displayPolicy.IsSupported || _displayPolicy.Displays.Count == 0)
		{
			throw new InvalidOperationException(_displayPolicy.UnsupportedReason ?? "Display topology policy selected no displays.");
		}
		if (DisplayPolicyLine != null)
		{
			_startBanner.Add(DisplayPolicyLine);
		}
		RecorderCore = ResolveRecorderCore();
		foreach (CaptureDisplaySettings display in _displayPolicy.Displays)
		{
			DisplayRecorder displayRecorder = new DisplayRecorder(display, _frameState, _router, RecorderCore)
			{
				CaptureFailed = OnCaptureFailed,
				CaptureAccessDenied = OnCaptureAccessDenied,
				LegacyPadCadence = LegacyPadCadence
			};
			displayRecorder.Start();
			_displays.Add(displayRecorder);
		}
		foreach (string item in _startBanner)
		{
			KuroLog.Capture(item, "StartCapture", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 99);
		}
		_startBanner.Clear();
		KuroLog.Capture($"SessionCaptureEngine started displayCount={_displays.Count} session={Path.GetFileName(_sessionDir)} topology={_displayPolicy.Mode} epoch={_displayPolicy.TopologyEpoch} physical_displays={_displayPolicy.PhysicalDisplayCount} logical_displays={_displayPolicy.LogicalDisplayCount} profile={_displayPolicy.Profile.Name}", "StartCapture", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 102);
	}

	private RecorderCoreKind ResolveRecorderCore()
	{
		RecorderCoreResolution recorderCoreResolution = RecorderCorePolicy.Resolve();
		RecorderCoreKind kind = recorderCoreResolution.Kind;
		RecorderCoreKind recorderCoreKind = kind;
		if (kind == RecorderCoreKind.GpuV2 && !Gen2Availability.TryProbe(_displayPolicy.Displays, out string unavailableReason))
		{
			recorderCoreKind = RecorderCoreKind.CpuV1;
			KuroLog.Capture($"recorder core fallback requested={"gpu_v2"} effective={"cpu_v1"} reason={unavailableReason}", "ResolveRecorderCore", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 126);
		}
		KuroLog.Capture($"recorder core resolved core={RecorderCorePolicy.Name(recorderCoreKind)} requested={RecorderCorePolicy.Name(kind)} source={recorderCoreResolution.Source} session={Path.GetFileName(_sessionDir)}", "ResolveRecorderCore", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 135);
		VideoBitrateSnapshot videoBitrateSnapshot = VideoBitratePolicy.Snapshot();
		_startBanner.Add(((object)videoBitrateSnapshot == null) ? ("video bitrate policy bpps=- min=- max=- source=fallback session=" + Path.GetFileName(_sessionDir)) : $"video bitrate policy bpps={videoBitrateSnapshot.BitsPerPixelPerSecond} min={videoBitrateSnapshot.MinMbps} max={videoBitrateSnapshot.MaxMbps} source={videoBitrateSnapshot.Source} session={Path.GetFileName(_sessionDir)}");
		return recorderCoreKind;
	}

	public SegmentSet CreateSegment(int segmentIndex, SegmentOpenReason reason, long clockCorrectionMs)
	{
		return new SegmentSet(_sessionDir, segmentIndex, _displays, _frameState, _displayPolicy, _router, reason, clockCorrectionMs, (Exception ex) =>
		{
			Task.Run(() =>
			{
				OnCaptureFailed(ex);
			});
		}, SystemAudio);
	}

	public async Task StopCaptureAsync()
	{
		KuroLog.Capture($"SessionCaptureEngine stopping displayCount={_displays.Count} session={Path.GetFileName(_sessionDir)}", "StopCaptureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 165);
		await Task.WhenAll(_displays.Select((DisplayRecorder d) => d.StopCaptureAndReleaseAsync())).ConfigureAwait(continueOnCapturedContext: false);
		_displays.Clear();
		KuroLog.Capture("SessionCaptureEngine stopped session=" + Path.GetFileName(_sessionDir), "StopCaptureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 168);
		KuroLog.Capture($"encoder leak summary leaked_writers={LeakedWriterCounter.Count} session={Path.GetFileName(_sessionDir)}", "StopCaptureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SessionCaptureEngine.cs", 169);
	}

	public async ValueTask DisposeAsync()
	{
		await StopCaptureAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private void OnCaptureFailed(Exception ex)
	{
		if (Interlocked.Exchange(ref _captureFailureRaised, 1) != 0)
		{
			return;
		}
		try
		{
			CaptureFailed?.Invoke(ex);
		}
		catch
		{
		}
	}

	private void OnCaptureAccessDenied()
	{
		if (Interlocked.Exchange(ref _captureAccessDeniedRaised, 1) != 0)
		{
			return;
		}
		try
		{
			CaptureAccessDenied?.Invoke();
		}
		catch
		{
		}
	}
}
