using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClientTelemetry;
using KuroWindows.Capture.Gen2;
using KuroWindows.Input;
using KuroWindows.RecorderCore;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.Sync;
using Vortice.Direct3D11;

namespace KuroWindows.Capture;

internal sealed class DisplayRecorder : IAsyncDisposable
{
	private readonly CaptureDisplaySettings _display;

	private readonly FrameStateSource? _frameState;

	private readonly RecorderWriteRouter _router;

	private readonly RecorderCoreKind _recorderCore;

	private DisplayCapture? _dxgi;

	private WgcMonitorCaptureBackend? _wgc;

	private GpuV2CaptureBackend? _gpuV2;

	private bool _started;

	public CaptureBackendKind Backend { get; private set; }

	public CapturePathSelection CapturePath { get; private set; } = CapturePathSelection.Dxgi("not_started");

	public int CaptureFps { get; private set; }

	public string FallbackMode { get; private set; } = "";

	public int DisplayIndex => _display.LogicalDisplayIndex;

	public CaptureDisplaySettings DisplaySettings => _display;

	public int OutputWidth => _gpuV2?.OutputWidth ?? _wgc?.OutputWidth ?? (_dxgi ?? throw new InvalidOperationException("DisplayRecorder has not been started.")).OutputWidth;

	public int OutputHeight => _gpuV2?.OutputHeight ?? _wgc?.OutputHeight ?? (_dxgi ?? throw new InvalidOperationException("DisplayRecorder has not been started.")).OutputHeight;

	public ID3D11Device? D3DDevice
	{
		get
		{
			ID3D11Device? iD3D11Device = _gpuV2?.D3DDevice;
			if ((object)iD3D11Device == null)
			{
				iD3D11Device = _wgc?.D3DDevice;
				if ((object)iD3D11Device == null)
				{
					DisplayCapture? dxgi = _dxgi;
					if (dxgi == null)
					{
						return null;
					}
					iD3D11Device = dxgi.D3DDevice;
				}
			}
			return iD3D11Device;
		}
	}

	public Action<Exception>? CaptureFailed { get; set; }

	public Action? CaptureAccessDenied { get; set; }

	public Func<bool>? LegacyPadCadence { get; init; }

	public DisplayRecorder(CaptureDisplaySettings display, FrameStateSource? frameState, RecorderWriteRouter router, RecorderCoreKind recorderCore = RecorderCoreKind.CpuV1)
	{
		_display = display;
		_frameState = frameState;
		_router = router;
		_recorderCore = recorderCore;
	}

	public void Start()
	{
		if (_started)
		{
			return;
		}
		string text = Environment.GetEnvironmentVariable("KURO_CAPTURE_BACKEND")?.Trim().ToLowerInvariant();
		Count.CaptureStartAttempt(1L);
		KuroLog.Capture($"DisplayRecorder start display={DisplayIndex} original_screen={_display.OriginalScreenIndex} role={_display.Role} bounds={_display.Screen.Bounds} capture={_display.CaptureWidth}x{_display.CaptureHeight} fps={_display.CaptureFps} resolution_mode={_display.ResolutionMode} forcedBackend={text ?? "auto"} core={RecorderCorePolicy.Name(_recorderCore)}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 69);
		string displayKey = CaptureDisplayKey.For(_display.Screen);
		List<Exception> list = new List<Exception>();
		List<string> list2 = new List<string>();
		string borderlessFailureReason = null;
		bool flag = false;
		foreach (SourcePlan item in CaptureHealthRegistry.ResolveStartOrder(displayKey, _recorderCore))
		{
			if (item.Source == "gpu_v2")
			{
				try
				{
					StartGpuV2();
					return;
				}
				catch (Exception ex) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex))
				{
					DisposeActiveCapture();
					throw new CaptureAccessDeniedException("Windows denied display capture access during gpu_v2 startup; retrying capture when the desktop is available.", ex);
				}
				catch (Exception ex2)
				{
					KuroLog.Capture($"gpu_v2 start failed display={DisplayIndex}; falling back to cpu_v1 backends: {KuroLog.ExceptionSummary(ex2)}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 110);
					Count.CaptureBackendFallback(CaptureBackend.CpuV1, 1L);
					DisposeActiveCapture();
					RecordStartFailure(displayKey, item.Source, ex2);
					list.Add(ex2);
					list2.Add(item.Source);
				}
				continue;
			}
			if (!flag)
			{
				flag = true;
				switch (text)
				{
				case "wgc":
					StartWgc(WgcBorderMode.RequireSuppressedBorder, "forced_wgc");
					return;
				case "wgc_bordered":
					StartWgc(WgcBorderMode.AllowVisibleBorder, "forced_wgc_bordered");
					return;
				case "dxgi":
					StartDxgi();
					return;
				}
				if (!string.IsNullOrWhiteSpace(text) && text != "auto")
				{
					KuroLog.Capture("Unknown KURO_CAPTURE_BACKEND=" + text + "; using auto mode.", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 144);
				}
			}
			try
			{
				switch (item.Source)
				{
				case "dxgi":
					StartDxgi();
					return;
				case "wgc_borderless":
					StartWgc(WgcBorderMode.RequireSuppressedBorder, item.PlanReason);
					return;
				case "wgc_bordered":
					StartWgc(WgcBorderMode.AllowVisibleBorder, item.PlanReason, BorderedBlockerReason(item.PlanReason, borderlessFailureReason));
					return;
				}
			}
			catch (Exception ex3) when (item.Source == "dxgi" && CaptureAccessDeniedClassifier.IsAccessDenied(ex3))
			{
				DisposeActiveCapture();
				throw new CaptureAccessDeniedException("Windows denied DXGI desktop-image access during startup; retrying capture when the desktop is available.", ex3);
			}
			catch (Exception ex4)
			{
				DisposeActiveCapture();
				if (item.Source == "wgc_borderless")
				{
					borderlessFailureReason = ((ex4 is WgcBorderUnavailableException ex5) ? ex5.Reason : ex4.GetType().Name);
				}
				KuroLog.Capture($"capture_start source={item.Source} display={DisplayIndex} plan={item.PlanReason} outcome=start_failed: {KuroLog.ExceptionSummary(ex4)}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 195);
				RecordStartFailure(displayKey, item.Source, ex4);
				list.Add(ex4);
				list2.Add(item.Source);
			}
		}
		CaptureHealthRegistry.RecordLadderExhaustion(displayKey, RemoteSessionProbe.Kind);
		Count.CaptureStartFail(CaptureStartFailReason.BackendUnavailable, 1L);
		object obj;
		if (list.Count <= 0)
		{
			obj = null;
		}
		else
		{
			obj = list[list.Count - 1];
		}
		Exception ex6 = (Exception)obj;
		long? displayIndex = DisplayIndex;
		string errorCode = ex6?.GetType().Name;
		string errorMessage = ex6?.Message;
		Telemetry.Emit(Ev.CaptureStartFailed(displayIndex, null, errorCode, errorMessage));
		throw new InvalidOperationException($"All capture sources failed to start for display {DisplayIndex} (tried: {string.Join(",", list2)}).", (list.Count == 1) ? list[0] : new AggregateException(list));
	}

	public async Task StopAsync()
	{
		if (_gpuV2 != null)
		{
			await _gpuV2.StopAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		if (_wgc != null)
		{
			await _wgc.StopAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		if (_dxgi != null)
		{
			await _dxgi.StopAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	public async Task StopCaptureAndReleaseAsync()
	{
		await StopAsync().ConfigureAwait(continueOnCapturedContext: false);
		DisposeActiveCapture();
	}

	private void StartGpuV2()
	{
		GpuV2CaptureBackend gpuV2CaptureBackend = new GpuV2CaptureBackend(_display, LegacyPadCadence);
		try
		{
			gpuV2CaptureBackend.InitializeDevice();
			StartCommon();
			gpuV2CaptureBackend.AttachRouter(_router, _frameState);
			gpuV2CaptureBackend.CaptureFailed = (Exception ex) =>
			{
				CaptureFailed?.Invoke(ex);
			};
			gpuV2CaptureBackend.CaptureAccessDenied = CaptureAccessDenied;
			_gpuV2 = gpuV2CaptureBackend;
			gpuV2CaptureBackend.Start();
			Backend = gpuV2CaptureBackend.ActiveBackendKind;
			CapturePath = gpuV2CaptureBackend.ActiveCapturePath;
			_started = true;
			KuroLog.Capture($"DisplayRecorder started display={DisplayIndex} backend=gpu_v2 frontend={gpuV2CaptureBackend.ActiveFrontEndName} capture_path={CapturePath.PathName} border_visible={CapturePath.CaptureBorderVisible.ToString().ToLowerInvariant()} size={gpuV2CaptureBackend.OutputWidth}x{gpuV2CaptureBackend.OutputHeight}", "StartGpuV2", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 255);
		}
		catch
		{
			if (_gpuV2 == gpuV2CaptureBackend)
			{
				_gpuV2 = null;
			}
			gpuV2CaptureBackend.Dispose();
			throw;
		}
	}

	private void StartWgc(WgcBorderMode borderMode, string selectionReason, string? borderBlockerReason = null)
	{
		WgcMonitorCaptureBackend wgcMonitorCaptureBackend = new WgcMonitorCaptureBackend(_display, borderMode, LegacyPadCadence);
		try
		{
			wgcMonitorCaptureBackend.InitializeDevice();
			StartCommon();
			wgcMonitorCaptureBackend.AttachRouter(_router, _frameState);
			wgcMonitorCaptureBackend.CaptureFailed = (Exception ex) =>
			{
				CaptureFailed?.Invoke(ex);
			};
			wgcMonitorCaptureBackend.CaptureAccessDenied = CaptureAccessDenied;
			_wgc = wgcMonitorCaptureBackend;
			wgcMonitorCaptureBackend.Start();
			Backend = CaptureBackendKind.WindowsGraphicsCapture;
			CapturePath = CapturePathSelection.Wgc(borderMode, borderBlockerReason ?? WgcMonitorCaptureBackend.BorderSuppressionBlocker, selectionReason);
			_started = true;
			KuroLog.Capture($"DisplayRecorder started display={DisplayIndex} backend=wgc capture_path={CapturePath.PathName} border_visible={CapturePath.CaptureBorderVisible.ToString().ToLowerInvariant()} size={wgcMonitorCaptureBackend.OutputWidth}x{wgcMonitorCaptureBackend.OutputHeight}", "StartWgc", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 299);
		}
		catch
		{
			if (_wgc == wgcMonitorCaptureBackend)
			{
				_wgc = null;
			}
			wgcMonitorCaptureBackend.Dispose();
			throw;
		}
	}

	private void StartDxgi()
	{
		DisplayCapture displayCapture = new DisplayCapture(_display, LegacyPadCadence);
		try
		{
			displayCapture.InitializeDevice();
			StartCommon();
			displayCapture.AttachRouter(_router, _frameState);
			string displayKey = CaptureDisplayKey.For(_display.Screen);
			displayCapture.CaptureFailed = (Exception ex) =>
			{
				RecordRuntimeBackendFailure(displayKey, "dxgi", "dxgi-capture-failed", ex);
				CaptureFailed?.Invoke(ex);
			};
			displayCapture.CaptureAccessDenied = CaptureAccessDenied;
			displayCapture.StallLegFireEnabled = () => DesktopFeatureFlags.Seeded.EncoderWatchdog;
			displayCapture.DeniedHoldCapSeconds = () => DesktopFeatureFlags.Seeded.CaptureRecovery?.DeniedHoldSeconds;
			_dxgi = displayCapture;
			displayCapture.Start();
			Backend = CaptureBackendKind.DesktopDuplication;
			CapturePath = CapturePathSelection.Dxgi();
			_started = true;
			KuroLog.Capture($"DisplayRecorder started display={DisplayIndex} backend=dxgi capture_path={CapturePath.PathName} size={displayCapture.OutputWidth}x{displayCapture.OutputHeight}", "StartDxgi", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 341);
		}
		catch
		{
			if (_dxgi == displayCapture)
			{
				_dxgi = null;
			}
			displayCapture.Dispose();
			throw;
		}
	}

	public SegmentWriter CreateSegmentWriter(string sessionDir, int segmentIndex, FrameStateSource? frameState, Action<Exception>? captureFreshnessFailed = null)
	{
		DisplayCapture dxgi = _dxgi;
		CpuCaptureInstrumentation.PipelineWindow pipelineWindow = dxgi?.OpenPipelineWindow();
		SegmentWriter segmentWriter = new SegmentWriter(sessionDir, segmentIndex, _display, Backend, OutputWidth, OutputHeight, CreateSegmentEncoder, CaptureFps, FallbackMode, frameState, BuildCaptureFreshnessFailureCallback(captureFreshnessFailed));
		object obj;
		if (pipelineWindow != null)
		{
			CpuCaptureInstrumentation.PipelineWindow window = pipelineWindow;
			obj = (Action)(() =>
			{
				dxgi.LogPipelineSummary(window);
			});
		}
		else
		{
			obj = null;
		}
		segmentWriter.Sealed = (Action?)obj;
		return segmentWriter;
	}

	private Action<Exception> BuildCaptureFreshnessFailureCallback(Action<Exception>? downstream)
	{
		return (Exception ex) =>
		{
			RecordCaptureFreshnessBackendFailure(ex);
			downstream?.Invoke(ex);
		};
	}

	private void RecordCaptureFreshnessBackendFailure(Exception ex)
	{
		if (ex is CaptureEncoderStallException)
		{
			KuroLog.Capture($"capture quality encoder stall recorded display={DisplayIndex} backend={Backend}; " + "preserving active capture frontend", "RecordCaptureFreshnessBackendFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 394);
		}
		else if (_gpuV2 != null)
		{
			_gpuV2.RecordRuntimeFailure("capture-freshness", ex);
		}
		else
		{
			string displayKey = CaptureDisplayKey.For(_display.Screen);
			if (_dxgi != null && Backend == CaptureBackendKind.DesktopDuplication)
			{
				RecordRuntimeBackendFailure(displayKey, "dxgi", "capture-freshness", ex);
			}
			else if (_wgc != null && Backend == CaptureBackendKind.WindowsGraphicsCapture)
			{
				RecordRuntimeBackendFailure(displayKey, CapturePath.CaptureBorderVisible ? "wgc_bordered" : "wgc_borderless", "capture-freshness", ex);
			}
		}
	}

	private static void RecordStartFailure(string displayKey, string source, Exception ex)
	{
		if (!(ex is WgcBorderUnavailableException) && !CaptureHealthRegistry.IsNeverRecorded(ex))
		{
			CaptureHealthRegistry.Record(displayKey, source, FailureClass.HardInit, "start", ex);
		}
	}

	private static void RecordRuntimeBackendFailure(string displayKey, string source, string sourceTag, Exception ex)
	{
		if (!CaptureHealthRegistry.IsNeverRecorded(ex))
		{
			CaptureHealthRegistry.Record(displayKey, source, ClassifyRuntimeFailure(ex), sourceTag, ex);
		}
	}

	private static FailureClass ClassifyRuntimeFailure(Exception ex)
	{
		if (ex is CaptureSourceStarvationException)
		{
			return FailureClass.Starvation;
		}
		if (CaptureHealthRegistry.IsInitialFrameStall(ex))
		{
			return FailureClass.NoFirstFrame;
		}
		return FailureClass.Runtime;
	}

	private static string? BorderedBlockerReason(string planReason, string? borderlessFailureReason)
	{
		if (!planReason.StartsWith("borderless_unavailable_", StringComparison.Ordinal))
		{
			return borderlessFailureReason;
		}
		return planReason;
	}

	private ISegmentVideoEncoder CreateSegmentEncoder(string outputPath, int width, int height, int captureFps)
	{
		if (_gpuV2 == null)
		{
			return new VideoEncoder(outputPath, width, height, captureFps);
		}
		return _gpuV2.CreateSegmentEncoder(outputPath, width, height, captureFps);
	}

	private void StartCommon()
	{
		CaptureFps = _display.CaptureFps;
		FallbackMode = _display.ResolutionMode;
	}

	private void DisposeActiveCapture()
	{
		if (_gpuV2 != null || _wgc != null || _dxgi != null)
		{
			KuroLog.Capture($"DisplayRecorder disposing active capture display={DisplayIndex} backend={Backend}", "DisposeActiveCapture", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayRecorder.cs", 494);
		}
		_gpuV2?.Dispose();
		_gpuV2 = null;
		_wgc?.Dispose();
		_wgc = null;
		_dxgi?.Dispose();
		_dxgi = null;
		_started = false;
	}

	public async ValueTask DisposeAsync()
	{
		await StopCaptureAndReleaseAsync().ConfigureAwait(continueOnCapturedContext: false);
	}
}
