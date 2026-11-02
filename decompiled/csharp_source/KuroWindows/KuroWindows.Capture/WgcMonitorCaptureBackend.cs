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
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace KuroWindows.Capture;

public sealed class WgcMonitorCaptureBackend : IDisposable
{
	private readonly record struct PreparedCommit(long TimestampTicks, long WallMs, TimestampKind TimestampKind, TimestampQuality TimestampQuality, CaptureFrameKind FrameKind, ulong VisualHash, int? ChangeScore, long SourceCaptureWallMs, long PendingGeneration, bool BlendCursor, bool CursorBlendChanged);

	private const int InitialFrameWatchdogSeconds = 10;

	private readonly Screen _screen;

	private readonly object _sync = new object();

	private readonly object _frameSync = new object();

	private readonly PipelineHeartbeat _heartbeat = new PipelineHeartbeat();

	private readonly Rectangle _outputBounds;

	private readonly int _sourceWidth;

	private readonly int _sourceHeight;

	private readonly int _sourceStride;

	private readonly int _outputWidth;

	private readonly int _outputHeight;

	private readonly int _outputStride;

	private readonly int _framesPerSecond;

	private readonly int _inputDisplayIndex;

	private readonly CaptureCadenceController _cadence;

	private readonly WgcBorderMode _borderMode;

	private readonly CursorRenderer _cursorRenderer = new CursorRenderer();

	private readonly string _displayKey;

	private readonly string _healthSource;

	private FrameStateSource? _frameState;

	private RecorderWriteRouter? _router;

	private IDXGIFactory1? _factory;

	private IDXGIAdapter1? _adapter;

	private ID3D11Device? _device;

	private ID3D11DeviceContext? _context;

	private IDirect3DDevice? _winrtDevice;

	private ID3D11Texture2D? _stagingTexture;

	private GraphicsCaptureItem? _item;

	private Direct3D11CaptureFramePool? _framePool;

	private GraphicsCaptureSession? _session;

	private CancellationTokenSource? _captureCts;

	private Task? _encodeTask;

	private PipelineWatchdog? _watchdog;

	private byte[]? _sourceBuffer;

	private byte[] _captureBuffer;

	private byte[] _latestBuffer;

	private byte[] _encodeBuffer;

	private bool _hasCapturedFrame;

	private bool _hasFirstFrame;

	private long _pendingFrameTimestampTicks;

	private long _pendingFrameWallMs;

	private TimestampKind _pendingTimestampKind;

	private TimestampQuality _pendingTimestampQuality;

	private CaptureFrameKind _pendingFrameKind;

	private ulong _pendingFrameVisualHash;

	private int? _pendingFrameChangeScore;

	private ulong _lastSourceFrameVisualHash;

	private bool _hasSourceFrameVisualHash;

	private readonly long _captureFrameIntervalQpc;

	private long _nextCaptureQpc;

	private long _producedFrameCount;

	private long _encodedFrameCount;

	private long _framesArrived;

	private long _framesRouterRejected;

	private volatile bool _disposed;

	private volatile bool _stateLockAbandoned;

	private int _failureRaised;

	private int _suppressedFailureLogged;

	private long _suppressedFailureCount;

	private volatile bool _inCommit;

	private SizeInt32 _currentContentSize;

	private long _lastCursorOverlaySkipLogTicks;

	private string? _lastCursorOverlaySkipReason;

	private long _lastPacingStallLogTicks;

	private readonly HashSet<string> _remoteLoggedCursorSkipCategories = new HashSet<string>();

	private string? _sessionBorderBlocker;

	private bool _sessionBorderBlockerPinned;

	private long _pendingSourceCaptureWallMs;

	private bool _encodeBufferHoldsCommittedFrame;

	private CursorFrameSample _lastCommittedCursorSample = CursorFrameSample.None;

	private int _lastCommittedCursorScreen;

	private long _reseedHandledFloorWallMs;

	private readonly object _processGate = new object();

	private volatile bool _deviceTeardown;

	private const long ReseedLazyDelayMs = 500L;

	private static int _cursorCaptureDisableFailureLogged;

	private static readonly object BorderProbeSync = new object();

	private static readonly TimeSpan TransientBorderBlockerTtl = TimeSpan.FromMinutes(15.0);

	private static bool _borderBlockerProbed;

	private static string? _borderBlockerSnapshot;

	private static DateTimeOffset _borderBlockerProbedAtUtc;

	public int DisplayIndex { get; }

	public int OutputWidth => _outputWidth;

	public int OutputHeight => _outputHeight;

	public ID3D11Device? D3DDevice => _device;

	public Action<Exception>? CaptureFailed { get; set; }

	public Action? CaptureAccessDenied { get; set; }

	public static string? BorderSuppressionBlocker
	{
		get
		{
			lock (BorderProbeSync)
			{
				if (_borderBlockerProbed && (!IsTransientBorderBlocker(_borderBlockerSnapshot) || DateTimeOffset.UtcNow - _borderBlockerProbedAtUtc <= TransientBorderBlockerTtl))
				{
					return _borderBlockerSnapshot;
				}
				string text = ProbeBorderSuppression();
				if (_borderBlockerProbed && !string.Equals(text, _borderBlockerSnapshot, StringComparison.Ordinal))
				{
					KuroLog.LocalOnly("capture", $"wgc border suppression re-probe changed blocker={_borderBlockerSnapshot ?? "none"} -> {text ?? "none"}", "BorderSuppressionBlocker", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1745);
				}
				_borderBlockerSnapshot = text;
				_borderBlockerProbedAtUtc = DateTimeOffset.UtcNow;
				_borderBlockerProbed = true;
				return _borderBlockerSnapshot;
			}
		}
	}

	public WgcMonitorCaptureBackend(CaptureDisplaySettings settings, WgcBorderMode borderMode = WgcBorderMode.RequireSuppressedBorder, Func<bool>? legacyPadCadence = null)
	{
		DisplayIndex = settings.LogicalDisplayIndex;
		_inputDisplayIndex = settings.OriginalScreenIndex;
		_screen = settings.Screen;
		_framesPerSecond = Math.Max(settings.CaptureFps, 1);
		_cadence = new CaptureCadenceController(_framesPerSecond, new PadCommitPolicy(_framesPerSecond, legacyPadCadence));
		_borderMode = borderMode;
		_displayKey = CaptureHealthRegistry.KeyFor(_screen);
		_healthSource = ((borderMode == WgcBorderMode.RequireSuppressedBorder) ? "wgc_borderless" : "wgc_bordered");
		_captureFrameIntervalQpc = Math.Max(1L, Stopwatch.Frequency / _framesPerSecond);
		Rectangle rectangle = (_outputBounds = settings.NativeBounds);
		_sourceWidth = NormalizeDimension(rectangle.Width);
		_sourceHeight = NormalizeDimension(rectangle.Height);
		checked
		{
			_sourceStride = _sourceWidth * 4;
			_outputWidth = NormalizeDimension(settings.CaptureWidth);
			_outputHeight = NormalizeDimension(settings.CaptureHeight);
			_outputStride = _outputWidth * 4;
			int num = _outputStride * _outputHeight;
			if (_sourceWidth != _outputWidth || _sourceHeight != _outputHeight)
			{
				_sourceBuffer = new byte[_sourceStride * _sourceHeight];
			}
			_captureBuffer = new byte[num];
			_latestBuffer = new byte[num];
			_encodeBuffer = new byte[num];
		}
	}

	public static bool IsSupported()
	{
		return GraphicsCaptureSession.IsSupported();
	}

	public void InitializeDevice()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			EnsureDevice();
		}
	}

	internal void AttachRouter(RecorderWriteRouter router, FrameStateSource? source)
	{
		if (source == null)
		{
			throw new InvalidOperationException("FrameStateSource is required for WGC cursor/video/sidecar alignment.");
		}
		if (_router != null)
		{
			_router.GateClosed -= OnRouterGateClosed;
		}
		_router = router;
		_frameState = source;
		router.GateClosed += OnRouterGateClosed;
	}

	private void OnRouterGateClosed()
	{
		lock (_frameSync)
		{
			Array.Clear(_latestBuffer);
			_hasCapturedFrame = false;
			_hasFirstFrame = false;
			_pendingSourceCaptureWallMs = 0L;
			_encodeBufferHoldsCommittedFrame = false;
		}
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
			if (!(_session != null))
			{
				EnsureCaptureSession();
				if (_disposed)
				{
					KuroLog.LocalOnly("capture", $"wgc start aborted display={DisplayIndex}: disposed during session bring-up; tearing down the fresh session", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 282);
					DisposeCaptureSession();
					throw new ObjectDisposedException(GetType().FullName);
				}
				RecorderWriteRouter router = _router;
				_captureCts = new CancellationTokenSource();
				_heartbeat.Beat();
				_encodeTask = Task.Run(() => EncodeLoop(_captureCts.Token));
				_watchdog = new PipelineWatchdog(DisplayIndex, _heartbeat, () => Volatile.Read(in _hasFirstFrame), router.GateOpenElapsedMsOrNegative, () => 10, OnPipelineStall, null, _captureCts.Token, null, null, () => DesktopFeatureFlags.Seeded.EncoderWatchdog);
				_session.StartCapture();
			}
		}
	}

	public async Task StopAsync()
	{
		Task encodeTask;
		Task watchdogTask;
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				_captureCts?.Cancel();
				encodeTask = _encodeTask;
				watchdogTask = _watchdog?.Completion;
				_encodeTask = null;
				_watchdog = null;
				_captureCts = null;
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}
		else
		{
			_stateLockAbandoned = true;
			KuroLog.LocalOnly("capture", $"wgc stop entry lock wedged display={DisplayIndex}: snapshotting lock-free and entering the bounded stop regime", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 349);
			encodeTask = Interlocked.Exchange(ref _encodeTask, null);
			watchdogTask = Interlocked.Exchange(ref _watchdog, null)?.Completion;
			Interlocked.Exchange(ref _captureCts, null)?.Cancel();
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
					KuroLog.Capture($"abandoning encode task wedged in encoder commit display={DisplayIndex} backend=wgc", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 368);
				}
				else
				{
					KuroLog.Capture($"encode task did not stop within {2}s (capture-side wedge) display={DisplayIndex} backend=wgc; waiting {10}s more before abandoning", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 373);
					if (!(await WaitBoundedAsync(encodeTask, 10).ConfigureAwait(continueOnCapturedContext: false)))
					{
						KuroLog.Capture($"abandoning encode task wedged capture-side display={DisplayIndex} backend=wgc", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 379);
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
				KuroLog.LocalOnly("capture", $"wgc stop proceeding without watchdog task exit display={DisplayIndex}", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 388);
			}
		}
		finally
		{
			if (TryEnterSyncForCleanup())
			{
				try
				{
					DisposeCaptureSession();
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
		return await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(continueOnCapturedContext: false) == task;
	}

	private bool TryEnterSyncForCleanup()
	{
		if (!_stateLockAbandoned)
		{
			Monitor.Enter(_sync);
			return true;
		}
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			return true;
		}
		KuroLog.LocalOnly("capture", $"wgc cleanup skipped display={DisplayIndex}: abandoned thread still holds the state lock", "TryEnterSyncForCleanup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 438);
		return false;
	}

	private void EnsureDevice()
	{
		if (_device != null && _context != null && _winrtDevice != null && _stagingTexture != null)
		{
			return;
		}
		DisposeDevice();
		_factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
		_adapter = TryResolveAdapter(_factory, _screen);
		string text = _adapter?.Description1.Description;
		FeatureLevel[] featureLevels = new FeatureLevel[4]
		{
			FeatureLevel.Level_11_1,
			FeatureLevel.Level_11_0,
			FeatureLevel.Level_10_1,
			FeatureLevel.Level_10_0
		};
		D3D11.D3D11CreateDevice(_adapter, (_adapter == null) ? DriverType.Hardware : DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, featureLevels, out ID3D11Device device, out FeatureLevel _, out ID3D11DeviceContext immediateContext);
		_device = device;
		_context = immediateContext;
		using IDXGIDevice iDXGIDevice = _device.QueryInterface<IDXGIDevice>();
		_winrtDevice = WgcInterop.CreateWinRtDevice(iDXGIDevice.NativePointer);
		KuroLog.Capture($"wgc device created display={DisplayIndex} adapter={text ?? "default-hardware"}", "EnsureDevice", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 484);
		RecreateStagingTexture();
	}

	private void EnsureCaptureSession()
	{
		if (!GraphicsCaptureSession.IsSupported())
		{
			SignalCaptureAccessDenied();
			throw new NotSupportedException("Windows Graphics Capture is not supported on this Windows build.");
		}
		if (!_sessionBorderBlockerPinned)
		{
			_sessionBorderBlocker = BorderSuppressionBlocker;
			_sessionBorderBlockerPinned = true;
		}
		string sessionBorderBlocker = _sessionBorderBlocker;
		if (sessionBorderBlocker != null && _borderMode == WgcBorderMode.RequireSuppressedBorder)
		{
			throw new WgcBorderUnavailableException(sessionBorderBlocker, "WGC refused: capture border cannot be suppressed (" + sessionBorderBlocker + "); use DXGI or bordered WGC.");
		}
		EnsureDevice();
		DisposeCaptureSession();
		_item = WgcInterop.CreateItemForMonitor(_screen);
		_currentContentSize = _item.Size;
		System.Drawing.Size size = NormalizeSize(_currentContentSize);
		if (size.Width != _sourceWidth || size.Height != _sourceHeight)
		{
			throw new InvalidOperationException($"WGC content size {size.Width}x{size.Height} does not match source size {_sourceWidth}x{_sourceHeight}.");
		}
		_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _currentContentSize);
		_framePool.FrameArrived += OnFrameArrived;
		_item.Closed += OnItemClosed;
		_session = _framePool.CreateCaptureSession(_item);
		TrySetCursorCapture(_session, DisplayIndex);
		bool flag = _borderMode == WgcBorderMode.RequireSuppressedBorder;
		if (flag)
		{
			_session.IsBorderRequired = false;
		}
		else
		{
			KuroLog.Capture($"WARNING WGC visible capture border enabled display={DisplayIndex} reason={_sessionBorderBlocker ?? "visible_border_requested"}", "EnsureCaptureSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 543);
		}
		KuroLog.Capture($"WGC session created display={DisplayIndex} border_suppressed={flag.ToString().ToLowerInvariant()} border_visible={(!flag).ToString().ToLowerInvariant()} cursor_capture_disabled=true", "EnsureCaptureSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 547);
	}

	private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
	{
		try
		{
			while (true)
			{
				Direct3D11CaptureFrame direct3D11CaptureFrame = sender.TryGetNextFrame();
				if ((object)direct3D11CaptureFrame == null)
				{
					break;
				}
				using (direct3D11CaptureFrame)
				{
					lock (_processGate)
					{
						if (_deviceTeardown || _disposed)
						{
							break;
						}
						ProcessFrame(direct3D11CaptureFrame);
					}
				}
			}
		}
		catch (Exception ex)
		{
			SignalCaptureFailure("wgc-frame-arrived", ex);
		}
	}

	private void ProcessFrame(Direct3D11CaptureFrame frame)
	{
		if (_disposed)
		{
			return;
		}
		Interlocked.Increment(ref _framesArrived);
		using ID3D11Texture2D iD3D11Texture2D = WgcInterop.GetTextureFromSurface(frame.Surface);
		Texture2DDescription description = iD3D11Texture2D.Description;
		SizeInt32 size = checked(new SizeInt32
		{
			Width = (int)description.Width,
			Height = (int)description.Height
		});
		SizeInt32 contentSize = frame.ContentSize;
		if (contentSize.Width != _currentContentSize.Width || contentSize.Height != _currentContentSize.Height)
		{
			_currentContentSize = contentSize;
			System.Drawing.Size size2 = NormalizeSize(contentSize);
			if (size2.Width != _sourceWidth || size2.Height != _sourceHeight)
			{
				SignalCaptureFailure("wgc-size-change", new InvalidOperationException($"WGC content size changed to {size2.Width}x{size2.Height}; segment restart required."));
				return;
			}
			try
			{
				_framePool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _currentContentSize);
			}
			catch (Exception ex)
			{
				SignalCaptureFailure("wgc-frame-pool-recreate", ex);
				return;
			}
		}
		System.Drawing.Size size3 = NormalizeSize(size);
		if (size3.Width < _sourceWidth || size3.Height < _sourceHeight)
		{
			SignalCaptureFailure("wgc-texture-size", new InvalidOperationException($"WGC texture {size.Width}x{size.Height} is smaller than source size {_sourceWidth}x{_sourceHeight}."));
			return;
		}
		long num = TimeSpanToQpc(frame.SystemRelativeTime);
		long num2 = RecordingClock.Shared.WallMsFromQpc(num);
		RecorderWriteRouter? router = _router;
		if (router == null || !router.CanAcceptFrame(num2, num2))
		{
			Interlocked.Increment(ref _framesRouterRejected);
		}
		else
		{
			if (ShouldSkipExpensiveReadback(num))
			{
				return;
			}
			CopyTextureToStaging(iD3D11Texture2D);
			ID3D11DeviceContext iD3D11DeviceContext = _context ?? throw new ObjectDisposedException("WgcMonitorCaptureBackend");
			iD3D11DeviceContext.Map(_stagingTexture, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mappedResource).CheckError();
			try
			{
				if (_sourceBuffer != null)
				{
					CopyFrame(mappedResource, _sourceBuffer, _sourceWidth, _sourceHeight, _sourceStride);
					BgraFrameScaler.DownscaleNearest(_sourceBuffer, _sourceWidth, _sourceHeight, _sourceStride, _captureBuffer, _outputWidth, _outputHeight, _outputStride);
				}
				else
				{
					CopyFrame(mappedResource, _captureBuffer, _outputWidth, _outputHeight, _outputStride);
				}
			}
			finally
			{
				iD3D11DeviceContext.Unmap(_stagingTexture, 0u);
			}
			PublishCapturedFrame(num, TimestampKind.WgcSystemRelativeTime, TimestampQuality.Authoritative);
		}
	}

	private void OnPipelineStall(CapturePipelineStallException stall)
	{
		if (stall.Stage == "initial_frame")
		{
			SignalCaptureFailure("wgc-watchdog", new CapturePipelineStallException(DisplayIndex, stall.SilentSeconds, stall.Stage, $"WGC produced no initial frames after {10}s. frames_arrived={Interlocked.Read(in _framesArrived)} router_rejected={Interlocked.Read(in _framesRouterRejected)} published={Interlocked.Read(in _producedFrameCount)} gate_open_ms={_router?.GateOpenElapsedMsOrNegative() ?? (-1)}"));
		}
		else
		{
			SignalCaptureFailure("wgc-pipeline-stall", new CapturePipelineStallException(DisplayIndex, stall.SilentSeconds, stall.Stage, $"encode loop produced no heartbeat for >{25}s (wedged native call) backend=wgc display={DisplayIndex} in_commit={(_inCommit ? "true" : "false")}"));
		}
	}

	private unsafe static void CopyFrame(MappedSubresource mapped, byte[] destination, int width, int height, int stride)
	{
		fixed (byte* ptr = destination)
		{
			byte* dataPointer = (byte*)mapped.DataPointer;
			for (int i = 0; i < height; i++)
			{
				byte* source = dataPointer + i * mapped.RowPitch;
				byte* destination2 = ptr + i * stride;
				Buffer.MemoryCopy(source, destination2, stride, checked(width * 4));
			}
		}
	}

	private void PublishCapturedFrame(long sourceQpc, TimestampKind timestampKind, TimestampQuality timestampQuality)
	{
		ulong num = FrameVisualFingerprint.ComputeSampledHash(_captureBuffer, _outputWidth, _outputHeight, _outputStride);
		int? pendingFrameChangeScore = (_hasSourceFrameVisualHash ? new int?(FrameVisualFingerprint.HammingDistance(_lastSourceFrameVisualHash, num)) : ((int?)null));
		long num2 = RecordingClock.Shared.WallMsFromQpc(sourceQpc);
		lock (_frameSync)
		{
			long num3 = _router?.EpisodeCaptureFloorWallMs ?? 0;
			if (num3 <= 0 || num2 < num3)
			{
				return;
			}
			byte[] latestBuffer = _latestBuffer;
			byte[] captureBuffer = _captureBuffer;
			_captureBuffer = latestBuffer;
			_latestBuffer = captureBuffer;
			_hasCapturedFrame = true;
			_hasFirstFrame = true;
			_pendingFrameTimestampTicks = sourceQpc;
			_pendingFrameWallMs = num2;
			_pendingSourceCaptureWallMs = num2;
			_pendingTimestampKind = timestampKind;
			_pendingTimestampQuality = timestampQuality;
			_pendingFrameKind = CaptureFrameKind.Source;
			_pendingFrameVisualHash = num;
			_pendingFrameChangeScore = pendingFrameChangeScore;
			_lastSourceFrameVisualHash = num;
			_hasSourceFrameVisualHash = true;
			_producedFrameCount++;
		}
		CaptureHealthRegistry.RecordFirstFrame(_displayKey, _healthSource);
	}

	private bool ShouldSkipExpensiveReadback(long sourceQpc)
	{
		long num = Interlocked.Read(in _nextCaptureQpc);
		if (num != 0L && sourceQpc < num)
		{
			return true;
		}
		Interlocked.Exchange(ref _nextCaptureQpc, sourceQpc + _captureFrameIntervalQpc);
		return false;
	}

	private bool TryPrepareCommit(PadCommitKind decidedKind, bool cursorBlendChanged, long nowQpc, out PreparedCommit prepared)
	{
		lock (_frameSync)
		{
			if (_hasCapturedFrame)
			{
				Buffer.BlockCopy(_latestBuffer, 0, _encodeBuffer, 0, _latestBuffer.Length);
				_encodeBufferHoldsCommittedFrame = false;
				prepared = new PreparedCommit(_pendingFrameTimestampTicks, _pendingFrameWallMs, _pendingTimestampKind, _pendingTimestampQuality, _pendingFrameKind, _pendingFrameVisualHash, _pendingFrameChangeScore, _pendingSourceCaptureWallMs, _producedFrameCount, BlendCursor: true, CursorBlendChanged: false);
				return true;
			}
			if (!_hasFirstFrame || decidedKind == PadCommitKind.PendingFrame)
			{
				prepared = default;
				return false;
			}
			PadFrameStamp padFrameStamp = PadCommitPolicy.CreatePadStamp(nowQpc);
			if (decidedKind == PadCommitKind.PixelTrueHeartbeat && _encodeBufferHoldsCommittedFrame)
			{
				prepared = new PreparedCommit(padFrameStamp.TimestampTicks, padFrameStamp.WallMs, padFrameStamp.TimestampKind, padFrameStamp.TimestampQuality, padFrameStamp.FrameKind, _lastSourceFrameVisualHash, 0, _pendingSourceCaptureWallMs, -1L, BlendCursor: false, CursorBlendChanged: false);
				return true;
			}
			Buffer.BlockCopy(_latestBuffer, 0, _encodeBuffer, 0, _latestBuffer.Length);
			_encodeBufferHoldsCommittedFrame = false;
			prepared = new PreparedCommit(padFrameStamp.TimestampTicks, padFrameStamp.WallMs, padFrameStamp.TimestampKind, padFrameStamp.TimestampQuality, padFrameStamp.FrameKind, _lastSourceFrameVisualHash, 0, _pendingSourceCaptureWallMs, -1L, BlendCursor: true, cursorBlendChanged);
			return true;
		}
	}

	private void ConsumePendingFrame(long generation)
	{
		lock (_frameSync)
		{
			if (_producedFrameCount == generation)
			{
				_hasCapturedFrame = false;
			}
		}
	}

	private bool HasPendingFrameForCadence(out bool hasFirstFrame)
	{
		lock (_frameSync)
		{
			hasFirstFrame = _hasFirstFrame;
			return _hasCapturedFrame;
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

	private void MaybeReseedForEpisode(long episodeFloorWallMs)
	{
		if (episodeFloorWallMs != _reseedHandledFloorWallMs)
		{
			long pendingSourceCaptureWallMs;
			lock (_frameSync)
			{
				pendingSourceCaptureWallMs = _pendingSourceCaptureWallMs;
			}
			if (pendingSourceCaptureWallMs >= episodeFloorWallMs)
			{
				_reseedHandledFloorWallMs = episodeFloorWallMs;
			}
			else if ((long)RecordingClock.Shared.NowMs() - episodeFloorWallMs >= 500)
			{
				_reseedHandledFloorWallMs = episodeFloorWallMs;
				ReseedCaptureSession();
			}
		}
	}

	private void ReseedCaptureSession()
	{
		long timestamp = Stopwatch.GetTimestamp();
		Exception ex = null;
		string value = "skipped_no_session";
		GraphicsCaptureSession graphicsCaptureSession = null;
		Direct3D11CaptureFramePool direct3D11CaptureFramePool = null;
		GraphicsCaptureItem item = null;
		lock (_sync)
		{
			if (!_disposed && _framePool != null && _item != null && _session != null)
			{
				graphicsCaptureSession = _session;
				_session = null;
				direct3D11CaptureFramePool = _framePool;
				item = _item;
			}
		}
		if (graphicsCaptureSession != null)
		{
			GraphicsCaptureSession graphicsCaptureSession2 = null;
			try
			{
				graphicsCaptureSession.Dispose();
				graphicsCaptureSession2 = direct3D11CaptureFramePool.CreateCaptureSession(item);
				TrySetCursorCapture(graphicsCaptureSession2, DisplayIndex);
				if (_borderMode == WgcBorderMode.RequireSuppressedBorder)
				{
					graphicsCaptureSession2.IsBorderRequired = false;
				}
				graphicsCaptureSession2.StartCapture();
			}
			catch (Exception ex2)
			{
				try
				{
					graphicsCaptureSession2?.Dispose();
				}
				catch
				{
				}
				graphicsCaptureSession2 = null;
				bool flag;
				lock (_sync)
				{
					flag = _disposed || (object)_framePool != direct3D11CaptureFramePool;
				}
				if (flag)
				{
					value = "skipped_torn_down";
				}
				else
				{
					ex = ex2;
					value = "failed";
				}
			}
			if (graphicsCaptureSession2 != null)
			{
				bool flag2 = false;
				lock (_sync)
				{
					if (!_disposed && (object)_framePool == direct3D11CaptureFramePool && _session == null)
					{
						_session = graphicsCaptureSession2;
						flag2 = true;
					}
				}
				if (flag2)
				{
					value = "recreated";
				}
				else
				{
					try
					{
						graphicsCaptureSession2.Dispose();
					}
					catch
					{
					}
					value = "skipped_torn_down";
				}
			}
		}
		long value2 = (Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency;
		KuroLog.LocalOnly("capture", $"wgc reseed display={DisplayIndex} outcome={value} ms={value2}", "ReseedCaptureSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1140);
		if (ex != null)
		{
			SignalCaptureFailure("wgc-reseed", ex);
		}
	}

	private async Task EncodeLoop(CancellationToken cancellationToken)
	{
		using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromTicks(10000000L / (long)Math.Max(_framesPerSecond, 1)));
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				long timestamp = Stopwatch.GetTimestamp();
				_heartbeat.Beat();
				RecorderWriteRouter router = _router;
				long episodeCaptureFloorWallMs = router.EpisodeCaptureFloorWallMs;
				if (episodeCaptureFloorWallMs <= 0)
				{
					continue;
				}
				MaybeReseedForEpisode(episodeCaptureFloorWallMs);
				long wallMs = RecordingClock.Shared.WallMsFromQpc(timestamp);
				MousePositionEntry mousePositionEntry = _frameState?.MousePositionForFrame(wallMs);
				Point point = ((mousePositionEntry == null) ? Cursor.Position : new Point(mousePositionEntry.X, mousePositionEntry.Y));
				PadCursorObservation cursor = PadCursorObservation.WithSystemSprite(point.X, point.Y);
				bool hasPendingFrame = HasPendingFrameForCadence(out var hasFirstFrame);
				CaptureCadenceDecision captureCadenceDecision = _cadence.OnWake(timestamp, hasFirstFrame, hasPendingFrame, HasRecentActivityForCadence(timestamp), cursor);
				if (captureCadenceDecision.Reanchored && captureCadenceDecision.LateMs > 2000 && timestamp - _lastPacingStallLogTicks >= Stopwatch.Frequency * 30)
				{
					_lastPacingStallLogTicks = timestamp;
					KuroLog.LocalOnly("capture", $"wgc encode pacing re-anchored display={DisplayIndex} late_ms={captureCadenceDecision.LateMs}", "EncodeLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1213);
				}
				if (!captureCadenceDecision.ShouldCommit || !TryPrepareCommit(captureCadenceDecision.Kind, captureCadenceDecision.CursorBlendChanged, timestamp, out var prepared))
				{
					continue;
				}
				long wallMs2 = prepared.WallMs;
				if (!router.CanAcceptFrame(wallMs2, prepared.SourceCaptureWallMs))
				{
					if (prepared.PendingGeneration >= 0)
					{
						ConsumePendingFrame(prepared.PendingGeneration);
						router.NoteFrameDropped(DisplayIndex);
					}
					continue;
				}
				int num;
				CursorFrameSample sample;
				if (prepared.BlendCursor)
				{
					MousePositionEntry mousePositionEntry2 = _frameState?.MousePositionForFrame(wallMs2);
					Point hotspot = ((mousePositionEntry2 == null) ? Cursor.Position : new Point(mousePositionEntry2.X, mousePositionEntry2.Y));
					num = mousePositionEntry2?.Screen ?? MonitorTable.Current.IndexOfNearest(hotspot.X, hotspot.Y);
					sample = CursorFrameSample.FromSystemCursor(hotspot, blended: false, in cursor);
					if (!_cursorRenderer.TryOverlayCursor(_encodeBuffer, _outputBounds, _outputStride, hotspot, in cursor, out sample, out string reason) && sample.HasCursor)
					{
						LogCursorOverlaySkipped(reason);
					}
				}
				else
				{
					sample = _lastCommittedCursorSample;
					num = _lastCommittedCursorScreen;
				}
				_inCommit = true;
				bool flag;
				try
				{
					flag = router.CommitFrame(new FrameCommitRequest(DisplayIndex, _encodeBuffer, prepared.TimestampTicks, wallMs2, prepared.SourceCaptureWallMs, prepared.TimestampKind, prepared.TimestampQuality, prepared.FrameKind, CaptureBackendKind.WindowsGraphicsCapture, prepared.VisualHash, prepared.ChangeScore, sample, num)
					{
						CursorBlendChanged = (prepared.CursorBlendChanged ? new bool?(true) : ((bool?)null))
					});
				}
				finally
				{
					_inCommit = false;
				}
				if (prepared.PendingGeneration >= 0)
				{
					ConsumePendingFrame(prepared.PendingGeneration);
				}
				if (flag)
				{
					_cadence.MarkCommitted(timestamp);
					_encodedFrameCount++;
					_lastCommittedCursorSample = sample;
					_lastCommittedCursorScreen = num;
					lock (_frameSync)
					{
						_encodeBufferHoldsCommittedFrame = _hasFirstFrame;
					}
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				SignalCaptureFailure("wgc-encode-loop", ex2);
				break;
			}
		}
	}

	private void OnItemClosed(GraphicsCaptureItem sender, object args)
	{
		SignalCaptureFailure("wgc-item-closed", new InvalidOperationException("WGC capture item closed."));
	}

	private void LogCursorOverlaySkipped(string reason)
	{
		string text = reason.Split(' ')[0];
		if (_remoteLoggedCursorSkipCategories.Add(text))
		{
			KuroLog.Capture($"wgc cursor overlay skipped display={DisplayIndex}: {reason}", "LogCursorOverlaySkipped", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1357);
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (!string.Equals(text, _lastCursorOverlaySkipReason, StringComparison.Ordinal) || timestamp - _lastCursorOverlaySkipLogTicks >= Stopwatch.Frequency * 5)
		{
			_lastCursorOverlaySkipReason = text;
			_lastCursorOverlaySkipLogTicks = timestamp;
			KuroLog.LocalOnly("capture", $"wgc cursor overlay skipped display={DisplayIndex}: {reason}", "LogCursorOverlaySkipped", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1370);
		}
	}

	private void SignalCaptureAccessDenied()
	{
		KuroLog.Capture($"wgc access denied display={DisplayIndex} remote_session={RemoteSessionProbe.LogValue}: graphics capture unsupported on this build", "SignalCaptureAccessDenied", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1376);
		try
		{
			CaptureAccessDenied?.Invoke();
		}
		catch
		{
		}
	}

	private void SignalCaptureFailure(string source, Exception ex)
	{
		if (Interlocked.Exchange(ref _failureRaised, 1) != 0)
		{
			Interlocked.Increment(ref _suppressedFailureCount);
			if (Interlocked.Exchange(ref _suppressedFailureLogged, 1) == 0)
			{
				KuroLog.LocalOnly("capture", $"wgc secondary failure suppressed source={source} display={DisplayIndex}: {KuroLog.ExceptionSummary(ex)}", "SignalCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1395);
			}
			return;
		}
		KuroLog.Capture($"{source} failure display={DisplayIndex}: {KuroLog.ExceptionSummary(ex)}", "SignalCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1399);
		if (!CaptureHealthRegistry.IsNeverRecorded(ex))
		{
			string displayKey = _displayKey;
			string healthSource = _healthSource;
			FailureClass cls = ((ex is CaptureSourceStarvationException) ? FailureClass.Starvation : ((!CaptureHealthRegistry.IsInitialFrameStall(ex)) ? FailureClass.Runtime : FailureClass.NoFirstFrame));
			CaptureHealthRegistry.Record(displayKey, healthSource, cls, source, ex);
		}
		try
		{
			CaptureFailed?.Invoke(ex);
		}
		catch
		{
		}
	}

	private void RecreateStagingTexture()
	{
		_stagingTexture?.Dispose();
		Texture2DDescription description = new Texture2DDescription
		{
			Width = (uint)Math.Max(_sourceWidth, 1),
			Height = (uint)Math.Max(_sourceHeight, 1),
			MipLevels = 1u,
			ArraySize = 1u,
			Format = Format.B8G8R8A8_UNorm,
			SampleDescription = new SampleDescription(1u, 0u),
			Usage = ResourceUsage.Staging,
			BindFlags = BindFlags.None,
			CPUAccessFlags = CpuAccessFlags.Read,
			MiscFlags = ResourceOptionFlags.None
		};
		_stagingTexture = _device.CreateTexture2D(in description);
	}

	private void CopyTextureToStaging(ID3D11Texture2D texture)
	{
		Box value = new Box(0, 0, 0, _sourceWidth, _sourceHeight, 1);
		_context.CopySubresourceRegion(_stagingTexture, 0u, 0u, 0u, 0u, texture, 0u, value);
	}

	private void DisposeCaptureSession()
	{
		if (_framePool != null)
		{
			try
			{
				_framePool.FrameArrived -= OnFrameArrived;
			}
			catch
			{
			}
		}
		if (_item != null)
		{
			try
			{
				_item.Closed -= OnItemClosed;
			}
			catch
			{
			}
		}
		_session?.Dispose();
		_framePool?.Dispose();
		_session = null;
		_framePool = null;
		_item = null;
	}

	private void DisposeDevice()
	{
		DisposeCaptureSession();
		_stagingTexture?.Dispose();
		_stagingTexture = null;
		_context?.Dispose();
		_context = null;
		_device?.Dispose();
		_device = null;
		_adapter?.Dispose();
		_adapter = null;
		_factory?.Dispose();
		_factory = null;
		_winrtDevice?.Dispose();
		_winrtDevice = null;
	}

	public void Dispose()
	{
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				if (_disposed)
				{
					return;
				}
				_captureCts?.Cancel();
				_disposed = true;
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}
		else
		{
			if (_disposed)
			{
				return;
			}
			_stateLockAbandoned = true;
			KuroLog.LocalOnly("capture", $"wgc dispose entry lock wedged display={DisplayIndex}: entering the bounded cleanup regime", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1532);
			_captureCts?.Cancel();
			_disposed = true;
		}
		RecorderWriteRouter router = _router;
		if (router != null)
		{
			router.GateClosed -= OnRouterGateClosed;
		}
		try
		{
			Task? encodeTask = _encodeTask;
			if (encodeTask != null && !encodeTask.Wait(1000))
			{
				KuroLog.Capture($"wgc encode task stop timed out display={DisplayIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1550);
			}
		}
		catch
		{
		}
		try
		{
			PipelineWatchdog? watchdog = _watchdog;
			if (watchdog != null && !watchdog.Completion.Wait(1000))
			{
				KuroLog.Capture($"wgc watchdog task stop timed out display={DisplayIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1551);
			}
		}
		catch
		{
		}
		bool flag = Monitor.TryEnter(_processGate, TimeSpan.FromSeconds(2.0));
		if (flag)
		{
			try
			{
				_deviceTeardown = true;
			}
			finally
			{
				Monitor.Exit(_processGate);
			}
		}
		else
		{
			_deviceTeardown = true;
			KuroLog.LocalOnly("capture", $"wgc device teardown skipped display={DisplayIndex}: wedged frame callback still holds the process gate", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1579);
		}
		if (flag && TryEnterSyncForCleanup())
		{
			try
			{
				DisposeDevice();
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}
		_cursorRenderer.Dispose();
		KuroLog.Capture($"wgc capture summary display={DisplayIndex}: produced={_producedFrameCount} encoded={_encodedFrameCount}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1598);
		long num = Interlocked.Read(in _suppressedFailureCount);
		if (num > 1)
		{
			KuroLog.LocalOnly("capture", $"wgc secondary failures total suppressed_count={num} display={DisplayIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1602);
		}
	}

	private static IDXGIAdapter1? TryResolveAdapter(IDXGIFactory1 factory, Screen screen)
	{
		try
		{
			return ResolveAdapter(factory, screen);
		}
		catch (Exception ex)
		{
			KuroLog.Capture("WGC adapter resolve skipped for " + screen.DeviceName + "; using default hardware adapter: " + ex.Message, "TryResolveAdapter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1616);
			return null;
		}
	}

	private static IDXGIAdapter1 ResolveAdapter(IDXGIFactory1 factory, Screen screen)
	{
		long? displayConfigLuid = DxgiDisplayResolver.TryGetDisplayConfigAdapterLuid(screen);
		uint? num = null;
		int num2 = 0;
		IDXGIAdapter1 adapterOut;
		for (uint num3 = 0u; !factory.EnumAdapters1(num3, out adapterOut).Failure; num3++)
		{
			AdapterDescription1 description = adapterOut.Description1;
			long num4 = DxgiDisplayResolver.AdapterLuidToInt64(description.Luid.LowPart, description.Luid.HighPart);
			IDXGIOutput outputOut;
			for (uint num5 = 0u; !adapterOut.EnumOutputs(num5, out outputOut).Failure; num5++)
			{
				using (outputOut)
				{
					OutputDescription description2 = outputOut.Description;
					if ((bool)description2.AttachedToDesktop)
					{
						DxgiDisplayMatch dxgiDisplayMatch = DxgiDisplayResolver.Score(screen, description2, num4, displayConfigLuid);
						if (dxgiDisplayMatch.Score > num2)
						{
							num = num3;
							num2 = dxgiDisplayMatch.Score;
							KuroLog.Capture($"WGC adapter candidate display={screen.DeviceName} adapter={description.Description} adapter_luid=0x{num4:x16} output={description2.DeviceName} match={dxgiDisplayMatch.Reason} score={dxgiDisplayMatch.Score}", "ResolveAdapter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1654);
						}
					}
				}
			}
			adapterOut.Dispose();
		}
		if (num.HasValue)
		{
			factory.EnumAdapters1(num.Value, out var adapterOut2).CheckError();
			return adapterOut2;
		}
		throw new InvalidOperationException("Could not resolve WGC adapter for " + screen.DeviceName + ".");
	}

	private static System.Drawing.Size NormalizeSize(SizeInt32 size)
	{
		return new System.Drawing.Size(NormalizeDimension(size.Width), NormalizeDimension(size.Height));
	}

	private static int NormalizeDimension(int value)
	{
		return Math.Max(value & -2, 2);
	}

	private static long TimeSpanToQpc(TimeSpan relativeTime)
	{
		return (long)Math.Round((double)relativeTime.Ticks * ((double)Stopwatch.Frequency / 10000000.0));
	}

	private static void TrySetCursorCapture(GraphicsCaptureSession session, int displayIndex)
	{
		try
		{
			session.IsCursorCaptureEnabled = false;
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _cursorCaptureDisableFailureLogged, 1) == 0)
			{
				KuroLog.Capture($"wgc cursor capture disable unsupported display={displayIndex}: {ex.GetType().Name}", "TrySetCursorCapture", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\WgcMonitorCaptureBackend.cs", 1700);
			}
		}
	}

	private static bool IsTransientBorderBlocker(string? blocker)
	{
		if (blocker != null)
		{
			return !blocker.StartsWith("border_api_unavailable", StringComparison.Ordinal);
		}
		return false;
	}

	private static string? ProbeBorderSuppression()
	{
		try
		{
			if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
			{
				return "border_api_unavailable_requires_windows_10_20348_or_11";
			}
			AppCapabilityAccessStatus result = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).GetAwaiter().GetResult();
			return (result == AppCapabilityAccessStatus.Allowed) ? null : $"borderless_access_{result}".ToLowerInvariant();
		}
		catch (Exception ex)
		{
			return "borderless_probe_failed:" + ex.GetType().Name;
		}
	}
}
