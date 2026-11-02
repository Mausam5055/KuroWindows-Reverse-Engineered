using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.Sync;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace KuroWindows.Capture.Gen2;

internal sealed class GpuV2CaptureBackend : IDisposable, IGen2FrameSink
{
	private const int InitialFrameWatchdogSeconds = 3;

	private const int WgcInitialFrameWatchdogSeconds = 10;

	private const int WgcReseedLazyDelayMs = 500;

	private static readonly long ReseedRetryIntervalQpc = Stopwatch.Frequency;

	private static readonly TimeSpan SlotAcquireTimeout = TimeSpan.FromMilliseconds(15.0);

	private readonly Screen _screen;

	private readonly string _displayKey;

	private readonly object _sync = new object();

	private readonly object _frameSync = new object();

	private readonly object _processGate = new object();

	private bool _chainTeardown;

	private readonly Rectangle _outputBounds;

	private readonly int _outputWidth;

	private readonly int _outputHeight;

	private readonly int _framesPerSecond;

	private readonly int _inputDisplayIndex;

	private readonly long _captureFrameIntervalQpc;

	private readonly CaptureCadenceController _cadence;

	private readonly CursorRenderer _cursorRenderer = new CursorRenderer();

	private readonly CaptureInstrumentation? _instr = CaptureInstrumentation.CreateIfEnabled();

	private FrameStateSource? _frameState;

	private RecorderWriteRouter? _router;

	private GpuEncoderContext? _gpu;

	private IGen2FrameSource? _frameSource;

	private CancellationTokenSource? _captureCts;

	private Task? _encodeTask;

	private Task? _watchdogTask;

	private ID3D11Texture2D? _captureMailbox;

	private ID3D11Texture2D? _encodeMailbox;

	private ID3D11ShaderResourceView? _encodeMailboxSrv;

	private ID3D11UnorderedAccessView? _encodeMailboxCursorUav;

	private ID3D11Texture2D? _cursorTexture;

	private ID3D11ShaderResourceView? _cursorSrv;

	private ID3D11Buffer? _cursorConstants;

	private byte[]? _cursorSpriteBuffer;

	private int _cursorTextureSize;

	private bool _hasCapturedFrame;

	private bool _hasFirstFrame;

	private long _pendingFrameTimestampTicks;

	private long _pendingFrameWallMs;

	private TimestampKind _pendingTimestampKind;

	private TimestampQuality _pendingTimestampQuality;

	private CaptureFrameKind _pendingFrameKind;

	private long _seedCaptureWallMs;

	private long _encodeBufferSeedWallMs;

	private long _publishSeq;

	private bool _encodeMailboxDirty;

	private CursorFrameSample _lastCommittedCursor = CursorFrameSample.None;

	private int _lastCommittedCursorScreen;

	private long _reseedFloorWallMsSeen;

	private long _lastReseedRequestQpc;

	private long _nextCaptureQpc;

	private long _producedFrameCount;

	private long _encodedFrameCount;

	private long _poolStarvedTickCount;

	private long _poolStarvedSinceQpc;

	private long _lastPoolStarvedLogTicks;

	private volatile bool _inCommit;

	private bool _disposed;

	private int _failureRaised;

	private int _suppressedFailureLogged;

	private long _suppressedFailureCount;

	private int _accessDeniedSignaled;

	private volatile bool _sourceUnavailable;

	private long _lastCursorSkipLogTicks;

	private string? _lastCursorSkipReason;

	private readonly HashSet<string> _remoteLoggedCursorSkipCategories = new HashSet<string>(StringComparer.Ordinal);

	private static readonly string[] AutoLadder = new string[3] { "wgc_borderless", "wgc_bordered", "dda" };

	public int DisplayIndex { get; }

	public int OutputWidth => _outputWidth;

	public int OutputHeight => _outputHeight;

	public ID3D11Device? D3DDevice => _gpu?.Device;

	public CaptureBackendKind ActiveBackendKind => _frameSource?.BackendKind ?? CaptureBackendKind.WindowsGraphicsCapture;

	public string ActiveFrontEndName => _frameSource?.FrontEndName ?? "none";

	private string ActiveRuntimeFailureKey => _frameSource?.RuntimeFailureKey ?? "none";

	public CapturePathSelection ActiveCapturePath => _frameSource?.CapturePath ?? CapturePathSelection.Wgc(WgcBorderMode.RequireSuppressedBorder, null, "gpu_v2_not_started");

	public Action<Exception>? CaptureFailed { get; set; }

	public Action? CaptureAccessDenied { get; set; }

	public GpuV2CaptureBackend(CaptureDisplaySettings settings, Func<bool>? legacyPadCadence = null)
	{
		DisplayIndex = settings.LogicalDisplayIndex;
		_inputDisplayIndex = settings.OriginalScreenIndex;
		_screen = settings.Screen;
		_displayKey = CaptureHealthRegistry.KeyFor(_screen);
		_framesPerSecond = Math.Max(settings.CaptureFps, 1);
		_cadence = new CaptureCadenceController(_framesPerSecond, new PadCommitPolicy(_framesPerSecond, legacyPadCadence));
		_captureFrameIntervalQpc = Math.Max(1L, Stopwatch.Frequency / _framesPerSecond);
		_outputBounds = settings.NativeBounds;
		_outputWidth = settings.CaptureWidth & -2;
		_outputHeight = settings.CaptureHeight & -2;
	}

	public void RecordRuntimeFailure(string source, Exception ex)
	{
		string activeRuntimeFailureKey = ActiveRuntimeFailureKey;
		if (!(activeRuntimeFailureKey == "none"))
		{
			KuroLog.Capture($"gpu_v2 runtime failure recorded source={source} display={DisplayIndex} frontend={ActiveFrontEndName} failure_key={activeRuntimeFailureKey}: {ex.GetType().Name}: {ex.Message}", "RecordRuntimeFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 204);
			RecordHealthFailure(source, ex);
		}
	}

	public static bool IsNativeResolution(CaptureDisplaySettings settings)
	{
		if ((settings.CaptureWidth & -2) == (settings.NativeBounds.Width & -2))
		{
			return (settings.CaptureHeight & -2) == (settings.NativeBounds.Height & -2);
		}
		return false;
	}

	public void InitializeDevice()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			EnsureChain();
		}
	}

	internal void AttachRouter(RecorderWriteRouter router, FrameStateSource? source)
	{
		if (source == null)
		{
			throw new InvalidOperationException("FrameStateSource is required for gpu_v2 cursor/video/sidecar alignment.");
		}
		if (_router != null)
		{
			_router.GateClosed -= InvalidateMailboxOnGateClose;
		}
		_router = router;
		_frameState = source;
		router.GateClosed += InvalidateMailboxOnGateClose;
	}

	private void InvalidateMailboxOnGateClose()
	{
		if (Monitor.TryEnter(_frameSync, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				_hasCapturedFrame = false;
				_hasFirstFrame = false;
				_seedCaptureWallMs = 0L;
				_encodeBufferSeedWallMs = 0L;
				_publishSeq++;
				return;
			}
			finally
			{
				Monitor.Exit(_frameSync);
			}
		}
		Volatile.Write(ref _hasCapturedFrame, value: false);
		Volatile.Write(ref _hasFirstFrame, value: false);
		Interlocked.Exchange(ref _seedCaptureWallMs, 0L);
		Interlocked.Exchange(ref _encodeBufferSeedWallMs, 0L);
		KuroLog.LocalOnly("capture", $"gpu_v2 gate-close invalidation lock timed out display={DisplayIndex}; applied lock-free", "InvalidateMailboxOnGateClose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 295);
	}

	internal ISegmentVideoEncoder CreateSegmentEncoder(string outputPath, int width, int height, int captureFps)
	{
		return new TextureVideoEncoder(_gpu ?? throw new InvalidOperationException("gpu_v2 chain not initialized."), outputPath, DisplayIndex, width, height, captureFps);
	}

	public void Start()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_router == null)
			{
				throw new InvalidOperationException("Call AttachRouter before Start.");
			}
			if (_frameSource != null)
			{
				return;
			}
			EnsureChain();
			try
			{
				_captureCts = new CancellationTokenSource();
				PipelineHeartbeat heartbeat = new PipelineHeartbeat();
				_encodeTask = Task.Run(() => EncodeLoop(heartbeat, _captureCts.Token));
				_watchdogTask = new PipelineWatchdog(DisplayIndex, heartbeat, () => _hasFirstFrame, () => _router.GateOpenElapsedMsOrNegative(), CurrentInitialFrameWatchdogSeconds, OnWatchdogStall, HasStalePoolStarvationStreak, _captureCts.Token, () => _sourceUnavailable, () =>
				{
					Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
				}, () => DesktopFeatureFlags.Seeded.EncoderWatchdog).Completion;
				StartFrameSourceLadder();
			}
			catch
			{
				_captureCts?.Cancel();
				_captureCts = null;
				_encodeTask = null;
				_watchdogTask = null;
				_frameSource = null;
				throw;
			}
		}
	}

	private IGen2FrameSource StartFrameSourceLadder()
	{
		string text = Environment.GetEnvironmentVariable("KURO_GEN2_FRONTEND")?.Trim().ToLowerInvariant();
		bool flag;
		switch (text)
		{
		case "dda":
		case "wgc":
		case "wgc_borderless":
		case "wgc_bordered":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		string[] array2;
		if (flag)
		{
			string[] array = ((!(text == "wgc")) ? new string[1] { text } : new string[2] { "wgc_borderless", "wgc_bordered" });
			array2 = array;
			KuroLog.Capture($"gpu_v2 front-end forced display={DisplayIndex} frontend={text}", "StartFrameSourceLadder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 393);
		}
		else
		{
			if (!string.IsNullOrEmpty(text))
			{
				KuroLog.Capture("gpu_v2 unknown KURO_GEN2_FRONTEND=" + text + "; using the auto ladder", "StartFrameSourceLadder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 399);
			}
			array2 = AutoLadder;
		}
		List<string> list = new List<string>();
		for (int i = 0; i < array2.Length; i++)
		{
			IGen2FrameSource gen2FrameSource = CreateSourceForTier(array2[i]);
			string value = ((i + 1 < array2.Length) ? TierDisplayName(array2[i + 1]) : "cpu_v1");
			if (CaptureHealthRegistry.IsGen2TierDemoted(_displayKey, gen2FrameSource.RuntimeFailureKey, out string reason))
			{
				KuroLog.Capture($"gpu_v2 front-end fallback display={DisplayIndex} from={LadderLogName(gen2FrameSource)} to={value} reason={reason}", "StartFrameSourceLadder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 412);
				list.Add(gen2FrameSource.RuntimeFailureKey + ":" + reason);
				gen2FrameSource.Dispose();
				continue;
			}
			try
			{
				_frameSource = gen2FrameSource;
				gen2FrameSource.Start();
				return gen2FrameSource;
			}
			catch (WgcBorderUnavailableException ex)
			{
				_frameSource = null;
				gen2FrameSource.Dispose();
				KuroLog.Capture($"gpu_v2 front-end fallback display={DisplayIndex} from={LadderLogName(gen2FrameSource)} to={value} reason={ex.Reason}", "StartFrameSourceLadder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 430);
				list.Add(gen2FrameSource.RuntimeFailureKey + ":" + ex.Reason);
			}
			catch (Exception ex2)
			{
				if (CaptureAccessDeniedClassifier.IsAccessDenied(ex2))
				{
					string value2 = LadderLogName(gen2FrameSource);
					string runtimeFailureKey = gen2FrameSource.RuntimeFailureKey;
					string frontEndName = gen2FrameSource.FrontEndName;
					_frameSource = null;
					gen2FrameSource.Dispose();
					if (!TierCanFallBack(array2, i))
					{
						throw new CaptureAccessDeniedException("Windows denied desktop-image access for " + frontEndName + "; no GPU fallback tier remains.", ex2);
					}
					KuroLog.Capture($"gpu_v2 front-end fallback display={DisplayIndex} from={value2} to={value} reason=access_denied_{ex2.GetType().Name}", "StartFrameSourceLadder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 446);
					list.Add(runtimeFailureKey + ":access_denied");
				}
				else
				{
					_frameSource = null;
					gen2FrameSource.Dispose();
					CaptureHealthRegistry.RecordGen2TierFailure(_displayKey, gen2FrameSource.RuntimeFailureKey);
					KuroLog.Capture($"gpu_v2 front-end fallback display={DisplayIndex} from={LadderLogName(gen2FrameSource)} to={value} reason={ex2.Message}", "StartFrameSourceLadder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 461);
					list.Add(gen2FrameSource.RuntimeFailureKey + ":" + ex2.GetType().Name);
				}
			}
		}
		throw new InvalidOperationException($"No GPU front-end serviceable for display {DisplayIndex}: {string.Join("; ", list)}");
	}

	private IGen2FrameSource CreateSourceForTier(string tier)
	{
		if (!(tier == "dda"))
		{
			if (tier == "wgc_bordered")
			{
				return new WgcGen2FrameSource(this, _gpu, _screen, DisplayIndex, _outputWidth, _outputHeight, WgcBorderMode.AllowVisibleBorder);
			}
			return new WgcGen2FrameSource(this, _gpu, _screen, DisplayIndex, _outputWidth, _outputHeight);
		}
		return new DdaGen2FrameSource(this, _gpu, _screen, DisplayIndex, _outputWidth, _outputHeight);
	}

	private static bool TierCanFallBack(string[] tiers, int failedTierIndex)
	{
		return failedTierIndex + 1 < tiers.Length;
	}

	private static string TierDisplayName(string tier)
	{
		return tier switch
		{
			"dda" => "dda_gpu", 
			"wgc_borderless" => "wgc_borderless", 
			"wgc_bordered" => "wgc_bordered", 
			_ => tier, 
		};
	}

	private static string LadderLogName(IGen2FrameSource source)
	{
		CapturePathKind path = source.CapturePath.Path;
		if ((uint)(path - 1) > 1u)
		{
			return source.FrontEndName;
		}
		return source.CapturePath.PathName;
	}

	public async Task StopAsync()
	{
		Task encodeTask;
		Task watchdogTask;
		lock (_sync)
		{
			_captureCts?.Cancel();
			encodeTask = _encodeTask;
			watchdogTask = _watchdogTask;
			_encodeTask = null;
			_watchdogTask = null;
			_captureCts = null;
		}
		try
		{
			bool flag = encodeTask != null;
			if (flag)
			{
				flag = !(await WaitBoundedAsync(encodeTask, 2).ConfigureAwait(continueOnCapturedContext: false));
			}
			if (flag)
			{
				if (_inCommit)
				{
					KuroLog.Capture($"abandoning encode task wedged in encoder commit display={DisplayIndex} backend=gpu_v2", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 520);
				}
				else
				{
					KuroLog.Capture($"encode task did not stop within {2}s (capture-side wedge) display={DisplayIndex} backend=gpu_v2; waiting {10}s more before abandoning", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 525);
					if (!(await WaitBoundedAsync(encodeTask, 10).ConfigureAwait(continueOnCapturedContext: false)))
					{
						KuroLog.Capture($"abandoning encode task wedged capture-side display={DisplayIndex} backend=gpu_v2", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 531);
					}
				}
			}
			flag = watchdogTask != null;
			if (flag)
			{
				flag = !(await WaitBoundedAsync(watchdogTask, 2).ConfigureAwait(continueOnCapturedContext: false));
			}
			if (flag)
			{
				KuroLog.LocalOnly("capture", $"gpu_v2 stop proceeding without watchdog task exit display={DisplayIndex}", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 540);
			}
		}
		finally
		{
			IGen2FrameSource source;
			lock (_sync)
			{
				source = _frameSource;
				_frameSource = null;
			}
			if (source != null)
			{
				try
				{
					await source.StopAsync().ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					source.Dispose();
				}
			}
		}
	}

	private static async Task<bool> WaitBoundedAsync(Task task, int timeoutSeconds)
	{
		return await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(continueOnCapturedContext: false) == task;
	}

	private void EnsureChain()
	{
		ID3D11Device device;
		if (_gpu == null || !(_captureMailbox != null))
		{
			DisposeChain();
			if ((_outputWidth & -2) != (_outputBounds.Width & -2) || (_outputHeight & -2) != (_outputBounds.Height & -2))
			{
				throw new InvalidOperationException($"gpu_v2 requires native resolution display={DisplayIndex} capture={_outputWidth}x{_outputHeight} native={_outputBounds.Width}x{_outputBounds.Height}");
			}
			if (!GpuEncoderContext.TryCreate(_screen, _outputWidth, _outputHeight, out GpuEncoderContext gpuContext, out string failureReason))
			{
				throw new InvalidOperationException("gpu_v2 chain bring-up failed: " + failureReason);
			}
			_gpu = gpuContext;
			device = _gpu.Device;
			_captureMailbox = CreateMailbox();
			_encodeMailbox = CreateMailbox();
			ShaderResourceViewDescription value = new ShaderResourceViewDescription
			{
				Format = Format.B8G8R8A8_UNorm,
				ViewDimension = ShaderResourceViewDimension.Texture2D,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u,
					MostDetailedMip = 0u
				}
			};
			_encodeMailboxSrv = device.CreateShaderResourceView(_encodeMailbox, value);
			_encodeMailboxCursorUav = device.CreateUnorderedAccessView(_encodeMailbox, new UnorderedAccessViewDescription
			{
				Format = Format.R32_UInt,
				ViewDimension = UnorderedAccessViewDimension.Texture2D
			});
			_cursorConstants = device.CreateBuffer(new BufferDescription
			{
				ByteWidth = 32u,
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.ConstantBuffer,
				CPUAccessFlags = CpuAccessFlags.Write
			});
			KuroLog.Capture($"gpu_v2 chain ready display={DisplayIndex} size={_outputWidth}x{_outputHeight} mft=\"{_gpu.HardwareMftName}\" visual_fingerprint=disabled_hot_path", "EnsureChain", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 639);
			KuroLog.Capture($"gpu_v2 adapter display={DisplayIndex} name=\"{_gpu.AdapterName}\" luid=0x{_gpu.AdapterLuid:x}", "EnsureChain", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 646);
		}
		ID3D11Texture2D CreateMailbox()
		{
			ID3D11Device iD3D11Device = device;
			Texture2DDescription description = new Texture2DDescription
			{
				Width = (uint)_outputWidth,
				Height = (uint)_outputHeight,
				MipLevels = 1u,
				ArraySize = 1u,
				Format = Format.B8G8R8A8_Typeless,
				SampleDescription = new SampleDescription(1u, 0u),
				Usage = ResourceUsage.Default,
				BindFlags = (BindFlags.ShaderResource | BindFlags.UnorderedAccess)
			};
			return iD3D11Device.CreateTexture2D(in description);
		}
	}

	public bool ShouldDeliverFrame(long sourceQpc, long captureQpc, bool bypassPacing = false)
	{
		RecorderWriteRouter? router = _router;
		if (router == null || !router.CanAcceptFrame(RecordingClock.Shared.WallMsFromQpc(sourceQpc), RecordingClock.Shared.WallMsFromQpc(captureQpc)))
		{
			_instr?.RecordRouterReject();
			return false;
		}
		if (!bypassPacing && ShouldSkipCapture(sourceQpc))
		{
			_instr?.RecordGateReject();
			return false;
		}
		return true;
	}

	public void DeliverFrame(ID3D11Texture2D ownerDeviceTexture, long sourceQpc, long captureQpc, TimestampKind timestampKind, TimestampQuality timestampQuality)
	{
		long num = RecordingClock.Shared.WallMsFromQpc(captureQpc);
		lock (_processGate)
		{
			if (_disposed || _chainTeardown || _gpu == null)
			{
				return;
			}
			ID3D11DeviceContext immediateContext = _gpu.ImmediateContext;
			long num2 = ((_instr != null) ? Stopwatch.GetTimestamp() : 0);
			lock (_frameSync)
			{
				long num3 = _router?.EpisodeCaptureFloorWallMs ?? 0;
				if (num3 <= 0 || num < num3)
				{
					return;
				}
				long num4 = ((_instr != null) ? Stopwatch.GetTimestamp() : 0);
				_sourceUnavailable = false;
				immediateContext.CopySubresourceRegion(srcBox: new Box(0, 0, 0, _outputWidth, _outputHeight, 1), dstResource: _captureMailbox, dstSubresource: 0u, dstX: 0u, dstY: 0u, dstZ: 0u, srcResource: ownerDeviceTexture, srcSubresource: 0u);
				if (_instr != null)
				{
					_instr.RecordDeliver(_hasCapturedFrame, Stopwatch.GetTimestamp() - num4, num4 - num2);
				}
				_hasCapturedFrame = true;
				_hasFirstFrame = true;
				_pendingFrameTimestampTicks = sourceQpc;
				_pendingFrameWallMs = RecordingClock.Shared.WallMsFromQpc(sourceQpc);
				_pendingTimestampKind = timestampKind;
				_pendingTimestampQuality = timestampQuality;
				_pendingFrameKind = CaptureFrameKind.Source;
				_seedCaptureWallMs = num;
				_publishSeq++;
				_producedFrameCount++;
			}
		}
		Interlocked.Exchange(ref _accessDeniedSignaled, 0);
		CaptureHealthRegistry.RecordFirstFrame(_displayKey, ActiveFrontEndName);
	}

	public void DeliverCursorOnlyFrame(long sourceQpc)
	{
		if (_disposed)
		{
			return;
		}
		lock (_frameSync)
		{
			if (_hasFirstFrame && (!_hasCapturedFrame || _pendingFrameKind != CaptureFrameKind.Source))
			{
				_sourceUnavailable = false;
				_instr?.RecordDeliverCursorOnly();
				_hasCapturedFrame = true;
				_pendingFrameTimestampTicks = sourceQpc;
				_pendingFrameWallMs = RecordingClock.Shared.WallMsFromQpc(sourceQpc);
				_pendingTimestampKind = TimestampKind.DxgiLastMouseUpdateTime;
				_pendingTimestampQuality = TimestampQuality.Degraded;
				_pendingFrameKind = CaptureFrameKind.CursorOnly;
				_publishSeq++;
			}
		}
	}

	public void SourceFailed(string stage, Exception ex)
	{
		SignalCaptureFailure(stage, ex);
	}

	public void SourceAccessDenied()
	{
		MarkSourceUnavailable();
		SignalCaptureAccessDenied();
	}

	public void RecordAcquireOutcome(Gen2AcquireOutcome outcome, long accumulatedFrames)
	{
		_instr?.RecordAcquireOutcome(outcome, accumulatedFrames);
	}

	private bool ShouldSkipCapture(long sourceQpc)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = Interlocked.Read(in _nextCaptureQpc);
		if (num != 0L && timestamp < num)
		{
			return true;
		}
		Interlocked.Exchange(ref _nextCaptureQpc, timestamp + _captureFrameIntervalQpc);
		return false;
	}

	private void SnapshotMailboxForTick(out bool hasPendingFrame, out bool hasFirstFrame, out long seedCaptureWallMs, out bool sourceUnavailable)
	{
		lock (_frameSync)
		{
			hasPendingFrame = _hasCapturedFrame;
			hasFirstFrame = _hasFirstFrame;
			seedCaptureWallMs = _seedCaptureWallMs;
			sourceUnavailable = _sourceUnavailable;
		}
	}

	private void SettleConsumedFrame(long consumedSeq)
	{
		lock (_frameSync)
		{
			if (_publishSeq == consumedSeq)
			{
				_hasCapturedFrame = false;
			}
		}
	}

	internal static bool IsReseedDue(long floorWallMs, long seedCaptureWallMs, long nowWallMs, int lazyDelayMs)
	{
		if (floorWallMs > 0 && seedCaptureWallMs < floorWallMs)
		{
			return nowWallMs - floorWallMs >= lazyDelayMs;
		}
		return false;
	}

	private void MaybeRequestReseed(long nowQpc, long floorWallMs, long seedCaptureWallMs)
	{
		if (floorWallMs != _reseedFloorWallMsSeen)
		{
			_reseedFloorWallMsSeen = floorWallMs;
			_lastReseedRequestQpc = 0L;
		}
		IGen2FrameSource frameSource = _frameSource;
		if (frameSource != null)
		{
			int lazyDelayMs = ((frameSource.BackendKind != CaptureBackendKind.DesktopDuplication) ? 500 : 0);
			if (IsReseedDue(floorWallMs, seedCaptureWallMs, RecordingClock.Shared.WallMsFromQpc(nowQpc), lazyDelayMs) && (_lastReseedRequestQpc == 0L || nowQpc - _lastReseedRequestQpc >= ReseedRetryIntervalQpc))
			{
				_lastReseedRequestQpc = nowQpc;
				frameSource.RequestReseed();
			}
		}
	}

	private bool CommitPendingFrame(long nowQpc, in PadCursorObservation cursor)
	{
		long pendingFrameWallMs;
		long seedCaptureWallMs;
		long publishSeq;
		lock (_frameSync)
		{
			if (!_hasCapturedFrame)
			{
				Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
				return false;
			}
			pendingFrameWallMs = _pendingFrameWallMs;
			seedCaptureWallMs = _seedCaptureWallMs;
			publishSeq = _publishSeq;
		}
		RecorderWriteRouter? router = _router;
		if (router == null || !router.CanAcceptFrame(pendingFrameWallMs, seedCaptureWallMs))
		{
			SettleConsumedFrame(publishSeq);
			_router?.NoteFrameDropped(DisplayIndex);
			Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
			return false;
		}
		int num = _gpu.AcquireSlot(SlotAcquireTimeout);
		if (num < 0)
		{
			Interlocked.CompareExchange(ref _poolStarvedSinceQpc, nowQpc, 0L);
			_poolStarvedTickCount++;
			LogPoolStarvationIfNeeded();
			return false;
		}
		Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
		bool flag = false;
		try
		{
			long pendingFrameTimestampTicks;
			TimestampKind pendingTimestampKind;
			TimestampQuality pendingTimestampQuality;
			CaptureFrameKind pendingFrameKind;
			lock (_frameSync)
			{
				if (!_hasCapturedFrame)
				{
					return false;
				}
				long num2 = ((_instr != null) ? Stopwatch.GetTimestamp() : 0);
				_gpu.ImmediateContext.CopyResource(_encodeMailbox, _captureMailbox);
				if (_instr != null)
				{
					_instr.RecordEncodeCopy(Stopwatch.GetTimestamp() - num2);
					_instr.RecordConsume(fresh: true);
				}
				pendingFrameTimestampTicks = _pendingFrameTimestampTicks;
				pendingFrameWallMs = _pendingFrameWallMs;
				pendingTimestampKind = _pendingTimestampKind;
				pendingTimestampQuality = _pendingTimestampQuality;
				pendingFrameKind = _pendingFrameKind;
				seedCaptureWallMs = _seedCaptureWallMs;
				publishSeq = _publishSeq;
				_encodeBufferSeedWallMs = seedCaptureWallMs;
				_encodeMailboxDirty = true;
			}
			MousePositionEntry mousePositionEntry = _frameState?.MousePositionForFrame(pendingFrameWallMs);
			Point cursorPoint = ((mousePositionEntry == null) ? Cursor.Position : new Point(mousePositionEntry.X, mousePositionEntry.Y));
			int num3 = mousePositionEntry?.Screen ?? MonitorTable.Current.IndexOfNearest(cursorPoint.X, cursorPoint.Y);
			CursorFrameSample cursorFrameSample;
			lock (_frameSync)
			{
				cursorFrameSample = BlendCursorOnGpu(cursorPoint, in cursor);
				_gpu.ConvertToSlot(_encodeMailboxSrv, num);
			}
			_inCommit = true;
			try
			{
				flag = _router.CommitFrame(new FrameCommitRequest(DisplayIndex, Array.Empty<byte>(), pendingFrameTimestampTicks, pendingFrameWallMs, seedCaptureWallMs, pendingTimestampKind, pendingTimestampQuality, pendingFrameKind, ActiveBackendKind, 0uL, null, cursorFrameSample, num3)
				{
					GpuSampleSlot = num
				});
			}
			finally
			{
				_inCommit = false;
			}
			SettleConsumedFrame(publishSeq);
			if (flag)
			{
				_encodeMailboxDirty = false;
				_lastCommittedCursor = cursorFrameSample;
				_lastCommittedCursorScreen = num3;
			}
			return flag;
		}
		finally
		{
			if (!flag)
			{
				_gpu.ReturnUnsubmittedSlot(num);
			}
		}
	}

	private bool CommitPadFrame(PadCommitKind kind, bool cursorBlendChanged, long nowQpc, Point cursorPoint, int cursorScreen, in PadCursorObservation cursor, bool sourceUnavailable)
	{
		if (sourceUnavailable)
		{
			Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
			return false;
		}
		PadFrameStamp padFrameStamp = PadCommitPolicy.CreatePadStamp(nowQpc);
		bool flag = kind == PadCommitKind.BlendedPad || _encodeMailboxDirty;
		long sourceCaptureWallMs;
		lock (_frameSync)
		{
			sourceCaptureWallMs = (flag ? _seedCaptureWallMs : _encodeBufferSeedWallMs);
		}
		RecorderWriteRouter? router = _router;
		if (router == null || !router.CanAcceptFrame(padFrameStamp.WallMs, sourceCaptureWallMs))
		{
			Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
			return false;
		}
		int num = _gpu.AcquireSlot(SlotAcquireTimeout);
		if (num < 0)
		{
			Interlocked.CompareExchange(ref _poolStarvedSinceQpc, nowQpc, 0L);
			_poolStarvedTickCount++;
			LogPoolStarvationIfNeeded();
			return false;
		}
		Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
		bool flag2 = false;
		try
		{
			CursorFrameSample cursorFrameSample;
			if (flag)
			{
				lock (_frameSync)
				{
					if (!_hasFirstFrame)
					{
						return false;
					}
					long num2 = ((_instr != null) ? Stopwatch.GetTimestamp() : 0);
					_gpu.ImmediateContext.CopyResource(_encodeMailbox, _captureMailbox);
					if (_instr != null)
					{
						_instr.RecordEncodeCopy(Stopwatch.GetTimestamp() - num2);
						_instr.RecordConsume(fresh: false);
					}
					sourceCaptureWallMs = (_encodeBufferSeedWallMs = _seedCaptureWallMs);
					_encodeMailboxDirty = true;
				}
				lock (_frameSync)
				{
					cursorFrameSample = BlendCursorOnGpu(cursorPoint, in cursor);
					_gpu.ConvertToSlot(_encodeMailboxSrv, num);
				}
			}
			else
			{
				cursorFrameSample = _lastCommittedCursor;
				cursorScreen = _lastCommittedCursorScreen;
				lock (_frameSync)
				{
					_gpu.ConvertToSlot(_encodeMailboxSrv, num);
				}
			}
			_inCommit = true;
			try
			{
				flag2 = _router.CommitFrame(new FrameCommitRequest(DisplayIndex, Array.Empty<byte>(), padFrameStamp.TimestampTicks, padFrameStamp.WallMs, sourceCaptureWallMs, padFrameStamp.TimestampKind, padFrameStamp.TimestampQuality, padFrameStamp.FrameKind, ActiveBackendKind, 0uL, null, cursorFrameSample, cursorScreen)
				{
					GpuSampleSlot = num,
					CursorBlendChanged = (cursorBlendChanged ? new bool?(true) : ((bool?)null))
				});
			}
			finally
			{
				_inCommit = false;
			}
			if (flag2)
			{
				_encodeMailboxDirty = false;
				_lastCommittedCursor = cursorFrameSample;
				_lastCommittedCursorScreen = cursorScreen;
			}
			return flag2;
		}
		finally
		{
			if (!flag2)
			{
				_gpu.ReturnUnsubmittedSlot(num);
			}
		}
	}

	private bool HasRecentActivityForCadence(long nowQpc)
	{
		FrameStateSource frameState = _frameState;
		if (frameState == null)
		{
			return false;
		}
		long num = RecordingClock.Shared.WallMsFromQpc(nowQpc);
		return frameState.HasRecentActivityForDisplay(_inputDisplayIndex, num - 1000, num);
	}

	private async Task EncodeLoop(PipelineHeartbeat heartbeat, CancellationToken cancellationToken)
	{
		using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromTicks(10000000L / (long)Math.Max(_framesPerSecond, 1)));
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				long timestamp = Stopwatch.GetTimestamp();
				heartbeat.Beat();
				_instr?.MaybeFlush(timestamp, ActiveFrontEndName, DisplayIndex, (string m) =>
				{
					KuroLog.Capture(m, "EncodeLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1269);
				});
				long num = _router?.EpisodeCaptureFloorWallMs ?? 0;
				if (num == 0L)
				{
					Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
					continue;
				}
				SnapshotMailboxForTick(out var hasPendingFrame, out var hasFirstFrame, out var seedCaptureWallMs, out var sourceUnavailable);
				MaybeRequestReseed(timestamp, num, seedCaptureWallMs);
				long wallMs = RecordingClock.Shared.WallMsFromQpc(timestamp);
				MousePositionEntry mousePositionEntry = _frameState?.MousePositionForFrame(wallMs);
				Point cursorPoint = ((mousePositionEntry == null) ? Cursor.Position : new Point(mousePositionEntry.X, mousePositionEntry.Y));
				int cursorScreen = mousePositionEntry?.Screen ?? MonitorTable.Current.IndexOfNearest(cursorPoint.X, cursorPoint.Y);
				PadCursorObservation cursor = PadCursorObservation.WithSystemSprite(cursorPoint.X, cursorPoint.Y);
				CaptureCadenceDecision captureCadenceDecision = _cadence.OnWake(timestamp, hasFirstFrame, hasPendingFrame, HasRecentActivityForCadence(timestamp), cursor);
				if (!captureCadenceDecision.ShouldCommit)
				{
					Interlocked.Exchange(ref _poolStarvedSinceQpc, 0L);
				}
				else if ((captureCadenceDecision.Kind == PadCommitKind.PendingFrame) ? CommitPendingFrame(timestamp, in cursor) : CommitPadFrame(captureCadenceDecision.Kind, captureCadenceDecision.CursorBlendChanged, timestamp, cursorPoint, cursorScreen, in cursor, sourceUnavailable))
				{
					_cadence.MarkCommitted(timestamp);
					_encodedFrameCount++;
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				SignalCaptureFailure("gpu-v2-encode-loop", ex2);
				break;
			}
		}
	}

	private unsafe CursorFrameSample BlendCursorOnGpu(Point cursorPoint, in PadCursorObservation cursor)
	{
		CursorFrameSample result = CursorFrameSample.FromSystemCursor(cursorPoint, blended: false, in cursor);
		if (!_cursorRenderer.TryRenderCursorSprite(ref _cursorSpriteBuffer, out int spriteWidth, out int spriteHeight, out Point iconHotspot, out string reason))
		{
			LogCursorSkipped(reason);
			return result;
		}
		int num = cursorPoint.X - iconHotspot.X - _outputBounds.Left;
		int num2 = cursorPoint.Y - iconHotspot.Y - _outputBounds.Top;
		if (num + spriteWidth <= 0 || num2 + spriteHeight <= 0 || num >= _outputWidth || num2 >= _outputHeight)
		{
			return result;
		}
		try
		{
			ID3D11Device device = _gpu.Device;
			ID3D11DeviceContext immediateContext = _gpu.ImmediateContext;
			if (_cursorTexture == null || _cursorTextureSize < Math.Max(spriteWidth, spriteHeight))
			{
				_cursorSrv?.Dispose();
				_cursorTexture?.Dispose();
				_cursorTextureSize = Math.Max(64, Math.Max(spriteWidth, spriteHeight));
				_cursorTexture = device.CreateTexture2D(new Texture2DDescription
				{
					Width = (uint)_cursorTextureSize,
					Height = (uint)_cursorTextureSize,
					MipLevels = 1u,
					ArraySize = 1u,
					Format = Format.B8G8R8A8_UNorm,
					SampleDescription = new SampleDescription(1u, 0u),
					Usage = ResourceUsage.Default,
					BindFlags = BindFlags.ShaderResource
				});
				_cursorSrv = device.CreateShaderResourceView(_cursorTexture);
			}
			immediateContext.UpdateSubresource<byte>(region: new Box(0, 0, 0, spriteWidth, spriteHeight, 1), data: _cursorSpriteBuffer.AsSpan(0, spriteWidth * spriteHeight * 4), resource: _cursorTexture, subresource: 0u, rowPitch: (uint)(spriteWidth * 4));
			MappedSubresource mappedSubresource = immediateContext.Map(_cursorConstants, 0u, MapMode.WriteDiscard);
			try
			{
				int* dataPointer = (int*)mappedSubresource.DataPointer;
				*dataPointer = num;
				dataPointer[1] = num2;
				dataPointer[2] = spriteWidth;
				dataPointer[3] = spriteHeight;
				dataPointer[4] = _outputWidth;
				dataPointer[5] = _outputHeight;
				dataPointer[6] = 0;
				dataPointer[7] = 0;
			}
			finally
			{
				immediateContext.Unmap(_cursorConstants, 0u);
			}
			immediateContext.ClearState();
			immediateContext.CSSetShader(_gpu.CursorBlendShader);
			immediateContext.CSSetShaderResource(0u, _cursorSrv);
			immediateContext.CSSetConstantBuffer(0u, _cursorConstants);
			immediateContext.CSSetUnorderedAccessView(0u, _encodeMailboxCursorUav);
			immediateContext.Dispatch((uint)((spriteWidth + 7) / 8), (uint)((spriteHeight + 7) / 8), 1u);
			immediateContext.ClearState();
			return CursorFrameSample.FromSystemCursor(cursorPoint, blended: true, in cursor);
		}
		catch (Exception ex)
		{
			LogCursorSkipped("gpu-blend-failed:" + ex.GetType().Name);
			return result;
		}
	}

	private int CurrentInitialFrameWatchdogSeconds()
	{
		if (!(ActiveFrontEndName == "wgc_gpu"))
		{
			return 3;
		}
		return 10;
	}

	private bool HasStalePoolStarvationStreak()
	{
		long num = Interlocked.Read(in _poolStarvedSinceQpc);
		if (num != 0L)
		{
			return Stopwatch.GetTimestamp() - num > Stopwatch.Frequency * 25;
		}
		return false;
	}

	private void OnWatchdogStall(CapturePipelineStallException stall)
	{
		string stage = stall.Stage;
		if (!(stage == "initial_frame"))
		{
			if (stage == "pool_starvation")
			{
				long num = Interlocked.Read(in _poolStarvedSinceQpc);
				double silentSeconds = ((num != 0L) ? ((double)(Stopwatch.GetTimestamp() - num) / (double)Stopwatch.Frequency) : stall.SilentSeconds);
				SignalCaptureFailure("gpu-v2-pool-starvation", new CapturePipelineStallException(DisplayIndex, silentSeconds, stall.Stage, $"gpu_v2 pool released no slot for >{25}s with frames pending " + "(encoder stopped releasing samples)."));
			}
			else
			{
				SignalCaptureFailure("gpu-v2-pipeline-stall", new CapturePipelineStallException(DisplayIndex, stall.SilentSeconds, stall.Stage, $"gpu_v2 encode loop produced no heartbeat for >{25}s (wedged native call)."));
			}
		}
		else
		{
			int value = CurrentInitialFrameWatchdogSeconds();
			SignalCaptureFailure("gpu-v2-watchdog", new CapturePipelineStallException(DisplayIndex, stall.SilentSeconds, stall.Stage, $"gpu_v2 produced no initial frames after {value}s frontend={ActiveFrontEndName}."));
		}
	}

	private void LogCursorSkipped(string reason)
	{
		long timestamp = Stopwatch.GetTimestamp();
		if (!string.Equals(reason, _lastCursorSkipReason, StringComparison.Ordinal) || timestamp - _lastCursorSkipLogTicks >= Stopwatch.Frequency * 5)
		{
			_lastCursorSkipReason = reason;
			_lastCursorSkipLogTicks = timestamp;
			if (_remoteLoggedCursorSkipCategories.Add(reason.Split(' ')[0]))
			{
				KuroLog.Capture($"gpu_v2 cursor blend skipped display={DisplayIndex}: {reason}", "LogCursorSkipped", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1530);
			}
			else
			{
				KuroLog.LocalOnly("capture", $"gpu_v2 cursor blend skipped display={DisplayIndex}: {reason}", "LogCursorSkipped", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1534);
			}
		}
	}

	private void LogPoolStarvationIfNeeded()
	{
		long timestamp = Stopwatch.GetTimestamp();
		if (timestamp - _lastPoolStarvedLogTicks >= Stopwatch.Frequency * 5)
		{
			_lastPoolStarvedLogTicks = timestamp;
			KuroLog.Capture($"gpu_v2 pool starved display={DisplayIndex} skipped_ticks={_poolStarvedTickCount} " + "(encoder backpressure)", "LogPoolStarvationIfNeeded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1547);
		}
	}

	private void SignalCaptureAccessDenied()
	{
		if (Interlocked.Exchange(ref _accessDeniedSignaled, 1) != 0)
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

	private void MarkSourceUnavailable()
	{
		lock (_frameSync)
		{
			_sourceUnavailable = true;
			_hasCapturedFrame = false;
		}
	}

	private void SignalCaptureFailure(string source, Exception ex)
	{
		if (Interlocked.Exchange(ref _failureRaised, 1) != 0)
		{
			Interlocked.Increment(ref _suppressedFailureCount);
			if (Interlocked.Exchange(ref _suppressedFailureLogged, 1) == 0)
			{
				KuroLog.LocalOnly("capture", $"gpu_v2 secondary failure suppressed source={source} display={DisplayIndex}: {KuroLog.ExceptionSummary(ex)}", "SignalCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1587);
			}
			return;
		}
		KuroLog.Capture($"{source} failure display={DisplayIndex}: {KuroLog.ExceptionSummary(ex)}", "SignalCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1591);
		RecordHealthFailure(source, ex);
		try
		{
			CaptureFailed?.Invoke(ex);
		}
		catch
		{
		}
	}

	private void RecordHealthFailure(string sourceTag, Exception ex)
	{
		if (CaptureHealthRegistry.IsInitialFrameStall(ex))
		{
			string activeRuntimeFailureKey = ActiveRuntimeFailureKey;
			if (!(activeRuntimeFailureKey == "none"))
			{
				CaptureHealthRegistry.RecordGen2TierFailure(_displayKey, activeRuntimeFailureKey);
			}
		}
		else if (!CaptureHealthRegistry.IsNeverRecorded(ex))
		{
			if (ex is CaptureSourceStarvationException)
			{
				CaptureHealthRegistry.Record(_displayKey, ActiveFrontEndName, FailureClass.Starvation, sourceTag, ex);
			}
			else
			{
				CaptureHealthRegistry.RecordGen2TierFailure(_displayKey, ActiveRuntimeFailureKey);
			}
		}
	}

	private void DisposeChain()
	{
		_frameSource?.Dispose();
		_frameSource = null;
		_cursorSrv?.Dispose();
		_cursorSrv = null;
		_cursorTexture?.Dispose();
		_cursorTexture = null;
		_cursorConstants?.Dispose();
		_cursorConstants = null;
		_encodeMailboxCursorUav?.Dispose();
		_encodeMailboxCursorUav = null;
		_encodeMailboxSrv?.Dispose();
		_encodeMailboxSrv = null;
		_encodeMailbox?.Dispose();
		_encodeMailbox = null;
		_captureMailbox?.Dispose();
		_captureMailbox = null;
		_gpu?.ReleaseUser();
		_gpu = null;
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
			_captureCts?.Cancel();
			_disposed = true;
		}
		RecorderWriteRouter router = _router;
		if (router != null)
		{
			router.GateClosed -= InvalidateMailboxOnGateClose;
		}
		bool flag = true;
		bool flag2 = true;
		try
		{
			flag = _encodeTask?.Wait(1000) ?? true;
		}
		catch
		{
		}
		try
		{
			flag2 = _watchdogTask?.Wait(1000) ?? true;
		}
		catch
		{
		}
		if (!flag)
		{
			KuroLog.Capture($"gpu_v2 dispose timed out waiting for encode task display={DisplayIndex}; abandoning wedged thread", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1695);
		}
		if (!flag2)
		{
			KuroLog.Capture($"gpu_v2 dispose timed out waiting for watchdog task display={DisplayIndex}; abandoning wedged thread", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1699);
		}
		lock (_processGate)
		{
			_chainTeardown = true;
		}
		lock (_sync)
		{
			DisposeChain();
		}
		_cursorRenderer.Dispose();
		KuroLog.Capture($"gpu_v2 capture summary display={DisplayIndex}: produced={_producedFrameCount} encoded={_encodedFrameCount} pool_starved_ticks={_poolStarvedTickCount}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1718);
		long num = Interlocked.Read(in _suppressedFailureCount);
		if (num > 1)
		{
			KuroLog.LocalOnly("capture", $"gpu_v2 secondary failures total suppressed_count={num} display={DisplayIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\GpuV2CaptureBackend.cs", 1724);
		}
	}
}
