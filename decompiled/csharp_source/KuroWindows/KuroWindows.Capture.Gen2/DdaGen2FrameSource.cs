using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.Sync;
using SharpGen.Runtime;
using Vortice.DXGI;
using Vortice.Direct3D11;

namespace KuroWindows.Capture.Gen2;

internal sealed class DdaGen2FrameSource : IGen2FrameSource, IDisposable
{
	private const int AcquireTimeoutMs = 100;

	private const int SlotContentionRetryDelayMs = 1000;

	private const int DxgiErrorWaitTimeout = -2005270489;

	private const int DxgiErrorAccessLost = -2005270490;

	private const int DxgiErrorAccessDenied = -2005270485;

	private const int DxgiErrorInvalidCall = -2005270527;

	private const int DxgiErrorDeviceRemoved = -2005270523;

	private const int DxgiErrorDeviceReset = -2005270521;

	private readonly IGen2FrameSink _sink;

	private readonly GpuEncoderContext _gpu;

	private readonly Screen _screen;

	private readonly int _displayIndex;

	private readonly int _outputWidth;

	private readonly int _outputHeight;

	private readonly object _sync = new object();

	private readonly CaptureDeniedHold _deniedHold;

	private IDXGIOutput1? _output;

	private IDXGIOutputDuplication? _duplication;

	private CancellationTokenSource? _acquireCts;

	private Task? _acquireTask;

	private bool _deliveredFirstFrame;

	private bool _droppedPreSeedNoDesktopUpdateLogged;

	private bool _protectedContentLogged;

	private volatile bool _acquireTaskAbandoned;

	private volatile bool _reseedRequested;

	private bool _slotContentionLogged;

	private volatile bool _disposed;

	public string FrontEndName => "dda_gpu";

	public string RuntimeFailureKey => FrontEndName;

	public CaptureBackendKind BackendKind => CaptureBackendKind.DesktopDuplication;

	public CapturePathSelection CapturePath => CapturePathSelection.Dxgi("gpu_v2_dda");

	public DdaGen2FrameSource(IGen2FrameSink sink, GpuEncoderContext gpu, Screen screen, int displayIndex, int outputWidth, int outputHeight)
	{
		_sink = sink;
		_gpu = gpu;
		_screen = screen;
		_displayIndex = displayIndex;
		_outputWidth = outputWidth;
		_outputHeight = outputHeight;
		_deniedHold = new CaptureDeniedHold(displayIndex, DesktopFeatureFlags.Seeded.CaptureRecovery?.DeniedHoldSeconds, () =>
		{
		}, (int _) =>
		{
			_sink.SourceAccessDenied();
		}, RecreateDuplicationForHold);
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_sync)
		{
			if (_acquireTask == null)
			{
				EnsureDuplication();
				_acquireCts = new CancellationTokenSource();
				_acquireTask = Task.Run(() => AcquireLoop(_acquireCts.Token));
			}
		}
	}

	public async Task StopAsync()
	{
		Task acquireTask;
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				_acquireCts?.Cancel();
				acquireTask = _acquireTask;
				_acquireTask = null;
				_acquireCts = null;
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}
		else
		{
			_acquireTaskAbandoned = true;
			KuroLog.LocalOnly("capture", $"gpu_v2 DDA stop entry lock wedged display={_displayIndex}: snapshotting lock-free and entering the bounded stop regime", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 157);
			acquireTask = Interlocked.Exchange(ref _acquireTask, null);
			Interlocked.Exchange(ref _acquireCts, null)?.Cancel();
		}
		try
		{
			bool flag = acquireTask != null;
			if (flag)
			{
				flag = !(await WaitBoundedAsync(acquireTask, 2).ConfigureAwait(continueOnCapturedContext: false));
			}
			if (flag)
			{
				KuroLog.Capture($"capture task stop wedged display={_displayIndex} backend=gpu_v2_dda; waiting {10}s more before abandoning", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 178);
				if (!(await WaitBoundedAsync(acquireTask, 10).ConfigureAwait(continueOnCapturedContext: false)))
				{
					_acquireTaskAbandoned = true;
					KuroLog.Capture($"abandoning capture task display={_displayIndex} backend=gpu_v2_dda (wedged native call)", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 184);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (TryEnterSyncForCleanup())
			{
				try
				{
					DisposeDuplication();
				}
				finally
				{
					Monitor.Exit(_sync);
				}
			}
		}
	}

	private static async Task<bool> WaitBoundedAsync(Task task, int timeoutSeconds)
	{
		if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(continueOnCapturedContext: false) != task)
		{
			return false;
		}
		await task.ConfigureAwait(continueOnCapturedContext: false);
		return true;
	}

	private bool TryEnterSyncForCleanup()
	{
		if (!_acquireTaskAbandoned)
		{
			Monitor.Enter(_sync);
			return true;
		}
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			return true;
		}
		KuroLog.LocalOnly("capture", $"gpu_v2 DDA cleanup skipped display={_displayIndex}: abandoned acquire task still holds the state lock", "TryEnterSyncForCleanup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 247);
		return false;
	}

	private void EnsureDuplication()
	{
		if (!(_duplication != null))
		{
			if (_output == null)
			{
				_output = ResolveOutputOnChainAdapter();
			}
			_duplication = _output.DuplicateOutput(_gpu.Device);
			OutduplDescription description = _duplication.Description;
			if (description.Rotation != ModeRotation.Identity)
			{
				ModeRotation rotation = description.Rotation;
				DisposeDuplication();
				throw new NotSupportedException($"dda_gpu refused: display rotation {rotation} (v1 supports Identity only).");
			}
			int num = (int)description.ModeDescription.Width & -2;
			int num2 = (int)description.ModeDescription.Height & -2;
			if (num != _outputWidth || num2 != _outputHeight)
			{
				DisposeDuplication();
				throw new InvalidOperationException($"dda_gpu mode {num}x{num2} does not match output {_outputWidth}x{_outputHeight}.");
			}
			_protectedContentLogged = false;
			_deliveredFirstFrame = false;
			_droppedPreSeedNoDesktopUpdateLogged = false;
			_reseedRequested = false;
			_slotContentionLogged = false;
			KuroLog.Capture($"gpu_v2 DDA duplication created display={_displayIndex} size={_outputWidth}x{_outputHeight} rotation=identity", "EnsureDuplication", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 300);
		}
	}

	public void RequestReseed()
	{
		_reseedRequested = true;
	}

	private void RecreateDuplicationForHold()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				throw new OperationCanceledException();
			}
			DisposeDuplication();
			EnsureDuplication();
		}
	}

	private IDXGIOutput1 ResolveOutputOnChainAdapter()
	{
		using IDXGIDevice iDXGIDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
		using IDXGIAdapter iDXGIAdapter = iDXGIDevice.GetAdapter();
		using IDXGIAdapter1 iDXGIAdapter2 = iDXGIAdapter.QueryInterface<IDXGIAdapter1>();
		AdapterDescription1 description = iDXGIAdapter2.Description1;
		long adapterLuid = DxgiDisplayResolver.AdapterLuidToInt64(description.Luid.LowPart, description.Luid.HighPart);
		long? displayConfigLuid = DxgiDisplayResolver.TryGetDisplayConfigAdapterLuid(_screen);
		IDXGIOutput outputOut;
		for (uint num = 0u; iDXGIAdapter2.EnumOutputs(num, out outputOut).Success; num++)
		{
			using (outputOut)
			{
				OutputDescription description2 = outputOut.Description;
				if (!description2.AttachedToDesktop || !DxgiDisplayResolver.Matches(_screen, description2, adapterLuid, displayConfigLuid))
				{
					continue;
				}
				return outputOut.QueryInterface<IDXGIOutput1>();
			}
		}
		throw new InvalidOperationException("dda_gpu: output for " + _screen.DeviceName + " not on the chain's adapter.");
	}

	private async Task AcquireLoop(CancellationToken cancellationToken)
	{
		int pendingDeniedHr = 0;
		bool pendingContentionPace = false;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				if (pendingContentionPace)
				{
					pendingContentionPace = false;
					await Task.Delay(1000, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (pendingDeniedHr != 0)
				{
					int entryHResult = pendingDeniedHr;
					pendingDeniedHr = 0;
					CaptureDeniedHoldResult captureDeniedHoldResult = await _deniedHold.RunAsync(entryHResult, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					if (captureDeniedHoldResult.Outcome == CaptureDeniedHoldOutcome.Cancelled)
					{
						break;
					}
					if (captureDeniedHoldResult.Outcome == CaptureDeniedHoldOutcome.Escalated)
					{
						_sink.SourceFailed("dda-duplication-recreate", captureDeniedHoldResult.EscalationException);
						break;
					}
				}
				AcquireOneFrame(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (SharpGenException ex2) when (ex2.HResult == -2005270489)
			{
			}
			catch (SharpGenException ex3) when (ex3.HResult == -2005270490)
			{
				KuroLog.Capture($"gpu_v2 DDA duplication reset display={_displayIndex}: {ex3.Message}", "AcquireLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 431);
				lock (_sync)
				{
					if (_disposed)
					{
						break;
					}
					DisposeDuplication();
					try
					{
						EnsureDuplication();
					}
					catch (Exception ex4) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex4))
					{
						KuroLog.Capture($"gpu_v2 DDA reset retry display={_displayIndex}: {ex4.Message}", "AcquireLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 443);
						pendingDeniedHr = CaptureAccessDeniedClassifier.LoggableHResult(ex4);
					}
					catch (Exception ex5) when (DuplicationSlotContentionClassifier.IsSlotContention(ex5))
					{
						LogSlotContentionOnce();
						pendingContentionPace = true;
					}
					catch (Exception ex6)
					{
						KuroLog.Capture($"gpu_v2 DDA reset retry display={_displayIndex}: {ex6.Message}", "AcquireLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 456);
					}
				}
			}
			catch (SharpGenException ex7) when (ex7.HResult == -2005270485)
			{
				pendingDeniedHr = ex7.HResult;
			}
			catch (SharpGenException ex8) when (ex8.HResult == -2005270527 || ex8.HResult == -2005270523 || ex8.HResult == -2005270521)
			{
				KuroLog.Capture($"gpu_v2 DDA device error display={_displayIndex} hr=0x{ex8.HResult:X8}: {ex8.Message}", "AcquireLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 472);
				lock (_sync)
				{
					if (_disposed)
					{
						break;
					}
					DisposeDuplication();
					_output?.Dispose();
					_output = null;
					try
					{
						EnsureDuplication();
					}
					catch (Exception ex9) when (DuplicationSlotContentionClassifier.IsSlotContention(ex9))
					{
						LogSlotContentionOnce();
						pendingContentionPace = true;
					}
					catch (Exception ex10)
					{
						_sink.SourceFailed("dda-duplication-recreate", ex10);
						break;
					}
				}
			}
			catch (Exception ex11) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex11))
			{
				pendingDeniedHr = CaptureAccessDeniedClassifier.LoggableHResult(ex11);
			}
			catch (Exception ex12) when (DuplicationSlotContentionClassifier.IsSlotContention(ex12))
			{
				LogSlotContentionOnce();
				pendingContentionPace = true;
			}
			catch (Exception ex13)
			{
				_sink.SourceFailed("dda-acquire-loop", ex13);
				break;
			}
		}
	}

	private void AcquireOneFrame(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IDXGIOutputDuplication duplication = _duplication;
		if (duplication == null || _reseedRequested)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				if (_reseedRequested)
				{
					long timestamp = Stopwatch.GetTimestamp();
					DisposeDuplication();
					try
					{
						EnsureDuplication();
						KuroLog.LocalOnly("capture", $"gpu_v2 DDA reseed display={_displayIndex} outcome=ok ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "AcquireOneFrame", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 553);
					}
					catch (Exception ex)
					{
						KuroLog.LocalOnly("capture", $"gpu_v2 DDA reseed display={_displayIndex} outcome={ex.GetType().Name} ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "AcquireOneFrame", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 560);
						throw;
					}
				}
				else
				{
					EnsureDuplication();
				}
				duplication = _duplication;
			}
		}
		OutduplFrameInfo frameInfo = default;
		IDXGIResource desktopResource = null;
		bool flag = false;
		try
		{
			Result result = duplication.AcquireNextFrame(100u, out frameInfo, out desktopResource);
			if (result.Code == -2005270489)
			{
				_sink.RecordAcquireOutcome(Gen2AcquireOutcome.Timeout, 0L);
				return;
			}
			result.CheckError();
			flag = true;
			if (desktopResource == null)
			{
				return;
			}
			if ((bool)frameInfo.ProtectedContentMaskedOut && !_protectedContentLogged)
			{
				_protectedContentLogged = true;
				KuroLog.Capture($"gpu_v2 DDA protected content masked display={_displayIndex}", "AcquireOneFrame", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 607);
			}
			DesktopDuplicationFrameUpdate desktopDuplicationFrameUpdate = DesktopDuplicationFrameClassifier.Classify(frameInfo.LastPresentTime, frameInfo.AccumulatedFrames, frameInfo.LastMouseUpdateTime);
			if (!desktopDuplicationFrameUpdate.HasDesktopUpdate)
			{
				_sink.RecordAcquireOutcome(Gen2AcquireOutcome.CursorOnly, frameInfo.AccumulatedFrames);
				if (_deliveredFirstFrame && desktopDuplicationFrameUpdate.HasPointerUpdate)
				{
					_sink.DeliverCursorOnlyFrame(frameInfo.LastMouseUpdateTime);
				}
				else if (!_deliveredFirstFrame && !_droppedPreSeedNoDesktopUpdateLogged)
				{
					_droppedPreSeedNoDesktopUpdateLogged = true;
					KuroLog.Capture($"gpu_v2 DDA dropped pre-seed non-desktop update display={_displayIndex} pointer_update={desktopDuplicationFrameUpdate.HasPointerUpdate.ToString().ToLowerInvariant()}", "AcquireOneFrame", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 629);
				}
				return;
			}
			_sink.RecordAcquireOutcome(Gen2AcquireOutcome.DesktopUpdate, frameInfo.AccumulatedFrames);
			long timestamp2 = Stopwatch.GetTimestamp();
			var (sourceQpc, timestampKind, timestampQuality) = DisplayCapture.SelectDesktopUpdateStamp(!_deliveredFirstFrame, frameInfo.LastPresentTime, timestamp2);
			if (!_sink.ShouldDeliverFrame(sourceQpc, timestamp2, !_deliveredFirstFrame))
			{
				return;
			}
			using ID3D11Texture2D iD3D11Texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();
			Texture2DDescription description = iD3D11Texture2D.Description;
			if ((int)description.Width < _outputWidth || (int)description.Height < _outputHeight)
			{
				_sink.SourceFailed("dda-size-change", new InvalidOperationException($"dda_gpu texture {description.Width}x{description.Height} is smaller than output {_outputWidth}x{_outputHeight}; segment restart required."));
			}
			else
			{
				_sink.DeliverFrame(iD3D11Texture2D, sourceQpc, timestamp2, timestampKind, timestampQuality);
				_deliveredFirstFrame = true;
			}
		}
		finally
		{
			desktopResource?.Dispose();
			if (flag)
			{
				try
				{
					duplication.ReleaseFrame();
				}
				catch
				{
				}
			}
		}
	}

	private void DisposeDuplication()
	{
		_duplication?.Dispose();
		_duplication = null;
	}

	private void LogSlotContentionOnce()
	{
		if (!_slotContentionLogged)
		{
			_slotContentionLogged = true;
			KuroLog.LocalOnly("capture", $"gpu_v2 DDA duplication slot contention display={_displayIndex} hr=0x{-2005270494:X8}; " + "holding on the reseed hedge (third-party capture holds the output slot)", "LogSlotContentionOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 721);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				_acquireCts?.Cancel();
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}
		else
		{
			_acquireTaskAbandoned = true;
			KuroLog.LocalOnly("capture", $"gpu_v2 DDA dispose entry lock wedged display={_displayIndex}: entering the bounded cleanup regime", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 764);
			_acquireCts?.Cancel();
		}
		bool flag = true;
		try
		{
			flag = _acquireTask?.Wait(1000) ?? true;
		}
		catch
		{
		}
		if (!flag)
		{
			_acquireTaskAbandoned = true;
			KuroLog.Capture($"gpu_v2 DDA dispose timed out waiting for acquire task display={_displayIndex}; abandoning wedged thread", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\DdaGen2FrameSource.cs", 776);
		}
		if (!TryEnterSyncForCleanup())
		{
			return;
		}
		try
		{
			DisposeDuplication();
			_output?.Dispose();
			_output = null;
		}
		finally
		{
			Monitor.Exit(_sync);
		}
	}
}
