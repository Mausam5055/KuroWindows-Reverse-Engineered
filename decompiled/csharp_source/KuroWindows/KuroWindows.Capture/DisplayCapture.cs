using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using SharpGen.Runtime;
using Vortice;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace KuroWindows.Capture;

public sealed class DisplayCapture : IDisposable
{
	private readonly record struct DxgiOutputCandidate(uint AdapterIndex, uint OutputIndex, string AdapterDescription, long AdapterLuid, string OutputDeviceName, Rectangle DesktopBounds, int Score, string MatchReason);

	private readonly record struct PreparedCommit(PointerSnapshot PointerSnapshot, long TimestampTicks, long WallMs, TimestampKind TimestampKind, TimestampQuality TimestampQuality, CaptureFrameKind FrameKind, ulong VisualHash, int? ChangeScore, long SeedCaptureWallMs, bool ConsumedMailbox, long SourceSeqSnapshot, long CursorOnlySeqSnapshot);

	private readonly record struct PointerSnapshot(bool Visible, bool HasShape, Point Position, OutduplPointerShapeInfo ShapeInfo, byte[] ShapeBuffer)
	{
		public static PointerSnapshot Create(bool visible, bool hasShape, Point position, OutduplPointerShapeInfo shapeInfo, byte[] shapeBuffer)
		{
			byte[] shapeBuffer2 = ((hasShape && shapeBuffer.Length != 0) ? shapeBuffer : Array.Empty<byte>());
			return new PointerSnapshot(visible, hasShape, position, shapeInfo, shapeBuffer2);
		}
	}

	private const int AcquireTimeoutMs = 100;

	private const int InitialFrameWatchdogSeconds = 10;

	private const int DxgiErrorWaitTimeout = -2005270489;

	private const int DxgiErrorAccessLost = -2005270490;

	private const int DxgiErrorAccessDenied = -2005270485;

	private const int DxgiErrorInvalidCall = -2005270527;

	private const int DxgiErrorDeviceRemoved = -2005270523;

	private const int DxgiErrorDeviceReset = -2005270521;

	private const int DxgiErrorMoreData = -2005270525;

	private readonly Screen _screen;

	private readonly string _displayKey;

	private readonly object _sync = new object();

	private readonly object _frameSync = new object();

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

	private readonly CursorRenderer _cursorRenderer = new CursorRenderer();

	private readonly CpuCaptureInstrumentation _instrument = new CpuCaptureInstrumentation();

	private readonly PipelineHeartbeat _heartbeat = new PipelineHeartbeat();

	private FrameStateSource? _frameState;

	private RecorderWriteRouter? _router;

	private IDXGIFactory1? _factory;

	private IDXGIAdapter1? _adapter;

	private IDXGIOutput1? _output;

	private IDXGIOutputDuplication? _duplication;

	private ID3D11Device? _device;

	private ID3D11DeviceContext? _context;

	private ID3D11Texture2D? _stagingTexture;

	private CancellationTokenSource? _captureCts;

	private Task? _captureTask;

	private Task? _encodeTask;

	private PipelineWatchdog? _watchdog;

	private CaptureDeniedHold? _deniedHold;

	private byte[]? _sourceBuffer;

	private byte[] _captureBuffer;

	private byte[] _latestBuffer;

	private byte[] _encodeBuffer;

	private byte[] _pointerShapeBuffer = Array.Empty<byte>();

	private OutduplPointerShapeInfo _pointerShapeInfo;

	private Point _pointerPosition;

	private bool _pointerVisible;

	private bool _hasPointerShape;

	private bool _hasCapturedFrame;

	private long _pendingFrameTimestampTicks;

	private long _pendingFrameWallMs;

	private TimestampKind _pendingTimestampKind;

	private TimestampQuality _pendingTimestampQuality;

	private CaptureFrameKind _pendingFrameKind;

	private ulong _pendingFrameVisualHash;

	private int? _pendingFrameChangeScore;

	private long _pendingSeedCaptureWallMs;

	private bool _hasCursorOnlyUpdate;

	private long _cursorOnlyUpdateQpc;

	private long _sourcePublishSeq;

	private long _cursorOnlyPublishSeq;

	private ulong _lastSourceFrameVisualHash;

	private bool _hasSourceFrameVisualHash;

	private bool _hasFirstFrame;

	private volatile bool _disposed;

	private int _frameArrivedErrors;

	private long _lastPointerLogTicks;

	private string? _lastPointerLogMessage;

	private long _producedFrameCount;

	private long _encodedFrameCount;

	private readonly long _captureFrameIntervalQpc;

	private long _nextCaptureQpc;

	private long _reseedHandledFloorWallMs;

	private long _lastReseedAttemptQpc;

	private bool _duplicationFirstFramePending;

	private bool _slotContentionLogged;

	private static readonly long ReseedRetryIntervalQpc = Stopwatch.Frequency;

	private const int SlotContentionRetryDelayMs = 1000;

	private bool _encodeBufferHoldsLastCommit;

	private CursorFrameSample _lastCommittedCursorSample = CursorFrameSample.None;

	private int _failureRaised;

	private int _accessDeniedSignaled;

	private bool _sourceUnavailable;

	private volatile bool _inCommit;

	private volatile bool _captureTaskAbandoned;

	private string? _adapterDescription;

	private long? _adapterLuid;

	private string? _outputDeviceName;

	private string? _outputMatchReason;

	private FeatureLevel _featureLevel;

	private bool _deviceFactsLogged;

	private readonly HashSet<(uint Adapter, uint Output, int HResult)> _duplicationCandidateFailuresLogged = new HashSet<(uint, uint, int)>();

	private long _lastPacingStallLogTicks;

	private readonly HashSet<string> _loggedOverlayFailureCategories = new HashSet<string>();

	public int DisplayIndex { get; }

	public int OutputWidth => _outputWidth;

	public int OutputHeight => _outputHeight;

	public ID3D11Device? D3DDevice => _device;

	public Action<Exception>? CaptureFailed { get; set; }

	public Action? CaptureAccessDenied { get; set; }

	internal Func<bool>? StallLegFireEnabled { get; set; }

	internal Func<int?>? DeniedHoldCapSeconds { get; set; }

	public DisplayCapture(CaptureDisplaySettings settings, Func<bool>? legacyPadCadence = null)
	{
		DisplayIndex = settings.LogicalDisplayIndex;
		_inputDisplayIndex = settings.OriginalScreenIndex;
		_screen = settings.Screen;
		_displayKey = CaptureHealthRegistry.KeyFor(_screen);
		_framesPerSecond = Math.Max(settings.CaptureFps, 1);
		_cadence = new CaptureCadenceController(_framesPerSecond, new PadCommitPolicy(_framesPerSecond, legacyPadCadence));
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

	public void InitializeDevice()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			EnsureDuplication();
		}
	}

	internal void AttachRouter(RecorderWriteRouter router, FrameStateSource? source)
	{
		if (source == null)
		{
			throw new InvalidOperationException("FrameStateSource is required for DXGI cursor/video/sidecar alignment.");
		}
		if (_router != null)
		{
			_router.GateClosed -= InvalidateMailboxForGateClose;
		}
		_router = router;
		_frameState = source;
		router.GateClosed += InvalidateMailboxForGateClose;
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
			if (_captureTask == null)
			{
				EnsureDuplication();
				if (_disposed)
				{
					KuroLog.LocalOnly("capture", $"dxgi start aborted display={DisplayIndex}: disposed during duplication bring-up; tearing down the fresh duplication", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 305);
					DisposeDuplication();
					throw new ObjectDisposedException(GetType().FullName);
				}
				RecorderWriteRouter router = _router;
				_captureCts = new CancellationTokenSource();
				_heartbeat.Beat();
				_captureTask = Task.Run(() => CaptureLoop(_captureCts.Token));
				_encodeTask = Task.Run(() => EncodeLoop(_captureCts.Token));
				_watchdog = new PipelineWatchdog(DisplayIndex, _heartbeat, () => Volatile.Read(in _hasFirstFrame), router.GateOpenElapsedMsOrNegative, () => 10, OnPipelineStall, null, _captureCts.Token, () => Volatile.Read(in _sourceUnavailable), null, () => StallLegFireEnabled?.Invoke() ?? true);
			}
		}
	}

	public async Task StopAsync()
	{
		Task task;
		Task encodeTask;
		PipelineWatchdog watchdog;
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				if (_captureTask == null && _encodeTask == null)
				{
					return;
				}
				_captureCts?.Cancel();
				task = _captureTask;
				encodeTask = _encodeTask;
				watchdog = _watchdog;
				_captureTask = null;
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
			_captureTaskAbandoned = true;
			KuroLog.LocalOnly("capture", $"dxgi stop entry lock wedged display={DisplayIndex}: snapshotting lock-free and entering the bounded stop regime", "StopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 383);
			task = Interlocked.Exchange(ref _captureTask, null);
			encodeTask = Interlocked.Exchange(ref _encodeTask, null);
			watchdog = Interlocked.Exchange(ref _watchdog, null);
			Interlocked.Exchange(ref _captureCts, null)?.Cancel();
			if (task == null && encodeTask == null)
			{
				return;
			}
		}
		try
		{
			await AwaitStopBoundedAsync(task, "capture", commitWedgeEligible: false, holdsSyncWhenWedged: true).ConfigureAwait(continueOnCapturedContext: false);
			await AwaitStopBoundedAsync(encodeTask, "encode", commitWedgeEligible: true, holdsSyncWhenWedged: false).ConfigureAwait(continueOnCapturedContext: false);
			await AwaitStopBoundedAsync(watchdog?.Completion, "watchdog", commitWedgeEligible: false, holdsSyncWhenWedged: false).ConfigureAwait(continueOnCapturedContext: false);
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

	private async Task AwaitStopBoundedAsync(Task? task, string taskName, bool commitWedgeEligible, bool holdsSyncWhenWedged)
	{
		if (task == null)
		{
			return;
		}
		if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2.0))).ConfigureAwait(continueOnCapturedContext: false) == task)
		{
			await task.ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (commitWedgeEligible && _inCommit)
		{
			Log($"abandoning encode task wedged in encoder commit display={DisplayIndex}", "AwaitStopBoundedAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 450);
			return;
		}
		Log($"{taskName} task stop wedged display={DisplayIndex} in_commit={(_inCommit ? "true" : "false")}; waiting {10}s more before abandoning", "AwaitStopBoundedAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 454);
		if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10.0))).ConfigureAwait(continueOnCapturedContext: false) == task)
		{
			await task.ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (holdsSyncWhenWedged)
		{
			_captureTaskAbandoned = true;
		}
		Log($"abandoning {taskName} task display={DisplayIndex} (wedged native call)", "AwaitStopBoundedAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 472);
	}

	private bool TryEnterSyncForCleanup()
	{
		if (!_captureTaskAbandoned)
		{
			Monitor.Enter(_sync);
			return true;
		}
		if (Monitor.TryEnter(_sync, TimeSpan.FromSeconds(2.0)))
		{
			return true;
		}
		KuroLog.LocalOnly("capture", $"dxgi cleanup skipped display={DisplayIndex}: abandoned capture task still holds the state lock", "TryEnterSyncForCleanup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 496);
		return false;
	}

	private void EnsureDuplication()
	{
		if (!(_duplication != null) || !(_device != null) || !(_context != null) || !(_stagingTexture != null))
		{
			DisposeDuplication();
			EnsureDevice();
		}
	}

	private void EnsureDevice()
	{
		CreateDeviceAndDuplication();
		OutduplDescription description = _duplication.Description;
		if (description.Rotation != ModeRotation.Identity)
		{
			ModeRotation rotation = description.Rotation;
			DisposeDuplication();
			throw new NotSupportedException($"DXGI duplication rotation {rotation} unsupported on display={DisplayIndex}; WGC handles rotated displays.");
		}
		RecreateStagingTexture();
		if (!_deviceFactsLogged)
		{
			_deviceFactsLogged = true;
			Log($"dxgi device created display={DisplayIndex} adapter={_adapterDescription ?? "-"} adapter_luid={FormatLuid(_adapterLuid)} feature_level={_featureLevel} output={_outputDeviceName ?? _screen.DeviceName} match={_outputMatchReason ?? "-"}", "EnsureDevice", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 538);
		}
	}

	private void EnsureDuplicationOnly()
	{
		if (_output == null || _device == null || _context == null || _stagingTexture == null)
		{
			DisposeDuplication();
			EnsureDevice();
			return;
		}
		CreateDuplication();
		OutduplDescription description = _duplication.Description;
		if (description.Rotation != ModeRotation.Identity || description.ModeDescription.Width != (uint)_sourceWidth || description.ModeDescription.Height != (uint)_sourceHeight)
		{
			KuroLog.LocalOnly("capture", $"duplication-only recreate mode change display={DisplayIndex} rotation={description.Rotation} mode={description.ModeDescription.Width}x{description.ModeDescription.Height} expected={_sourceWidth}x{_sourceHeight}; rebuilding device", "EnsureDuplicationOnly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 569);
			DisposeDuplication();
			EnsureDevice();
		}
	}

	private void CreateDeviceAndDuplication()
	{
		IReadOnlyList<DxgiOutputCandidate> readOnlyList = EnumerateOutputCandidates(_screen);
		List<Exception> list = new List<Exception>();
		foreach (DxgiOutputCandidate item in readOnlyList)
		{
			try
			{
				DisposeDuplication();
				_factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
				OpenCandidate(item);
				CreateDevice();
				CreateDuplication();
				return;
			}
			catch (Exception ex) when (IsDuplicationBringUpFailure(ex))
			{
				list.Add(ex);
				if (_duplicationCandidateFailuresLogged.Add((item.AdapterIndex, item.OutputIndex, ex.HResult)))
				{
					Log($"dxgi duplication candidate failed display={DisplayIndex} adapter_index={item.AdapterIndex} output_index={item.OutputIndex} adapter={item.AdapterDescription} adapter_luid={FormatLuid(item.AdapterLuid)} output={item.OutputDeviceName} bounds={FormatBounds(item.DesktopBounds)} match={item.MatchReason} score={item.Score} hr=0x{ex.HResult:X8} remote_session={RemoteSessionProbe.LogValue}: {ex.GetType().Name}: {ex.Message}", "CreateDeviceAndDuplication", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 600);
				}
			}
		}
		DisposeDuplication();
		if (list.Count > 0)
		{
			throw new AggregateException("No DXGI duplication candidate succeeded for " + _screen.DeviceName + ".", list);
		}
		throw new InvalidOperationException("Could not resolve DXGI output for " + _screen.DeviceName + ".");
	}

	private void OpenCandidate(DxgiOutputCandidate candidate)
	{
		_factory.EnumAdapters1(candidate.AdapterIndex, out var adapterOut).CheckError();
		_adapter = adapterOut;
		_adapterDescription = candidate.AdapterDescription;
		_adapterLuid = candidate.AdapterLuid;
		_adapter.EnumOutputs(candidate.OutputIndex, out var outputOut).CheckError();
		using (outputOut)
		{
			_output = outputOut.QueryInterface<IDXGIOutput1>();
		}
		_outputDeviceName = candidate.OutputDeviceName;
		_outputMatchReason = candidate.MatchReason;
	}

	private static IReadOnlyList<DxgiOutputCandidate> EnumerateOutputCandidates(Screen screen)
	{
		using IDXGIFactory1 iDXGIFactory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
		long? displayConfigLuid = DxgiDisplayResolver.TryGetDisplayConfigAdapterLuid(screen);
		List<DxgiOutputCandidate> list = new List<DxgiOutputCandidate>();
		IDXGIAdapter1 adapterOut;
		for (uint num = 0u; !iDXGIFactory.EnumAdapters1(num, out adapterOut).Failure; num++)
		{
			using (adapterOut)
			{
				AdapterDescription1 description = adapterOut.Description1;
				long adapterLuid = DxgiDisplayResolver.AdapterLuidToInt64(description.Luid.LowPart, description.Luid.HighPart);
				IDXGIOutput outputOut;
				for (uint num2 = 0u; !adapterOut.EnumOutputs(num2, out outputOut).Failure; num2++)
				{
					using (outputOut)
					{
						OutputDescription description2 = outputOut.Description;
						if ((bool)description2.AttachedToDesktop)
						{
							DxgiDisplayMatch dxgiDisplayMatch = DxgiDisplayResolver.Score(screen, description2, adapterLuid, displayConfigLuid);
							if (dxgiDisplayMatch.Score > 0)
							{
								list.Add(new DxgiOutputCandidate(num, num2, description.Description, adapterLuid, description2.DeviceName, DxgiDisplayResolver.DesktopBounds(description2), dxgiDisplayMatch.Score, dxgiDisplayMatch.Reason));
							}
						}
					}
				}
			}
		}
		return (from candidate in list
			orderby candidate.Score descending, candidate.AdapterIndex, candidate.OutputIndex
			select candidate).ToArray();
	}

	public static Rectangle ResolveNativeBoundsForScreen(Screen screen)
	{
		using IDXGIFactory1 iDXGIFactory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
		long? displayConfigLuid = DxgiDisplayResolver.TryGetDisplayConfigAdapterLuid(screen);
		IDXGIAdapter1 adapterOut;
		for (uint num = 0u; !iDXGIFactory.EnumAdapters1(num, out adapterOut).Failure; num++)
		{
			using (adapterOut)
			{
				IDXGIOutput outputOut;
				for (uint num2 = 0u; !adapterOut.EnumOutputs(num2, out outputOut).Failure; num2++)
				{
					using (outputOut)
					{
						OutputDescription description = outputOut.Description;
						if ((bool)description.AttachedToDesktop)
						{
							AdapterDescription1 description2 = adapterOut.Description1;
							long adapterLuid = DxgiDisplayResolver.AdapterLuidToInt64(description2.Luid.LowPart, description2.Luid.HighPart);
							if (DxgiDisplayResolver.Matches(screen, description, adapterLuid, displayConfigLuid))
							{
								RawRect desktopCoordinates = description.DesktopCoordinates;
								return Rectangle.FromLTRB(desktopCoordinates.Left, desktopCoordinates.Top, desktopCoordinates.Right, desktopCoordinates.Bottom);
							}
						}
					}
				}
			}
		}
		return screen.Bounds;
	}

	private void CreateDevice()
	{
		FeatureLevel[] featureLevels = new FeatureLevel[4]
		{
			FeatureLevel.Level_11_1,
			FeatureLevel.Level_11_0,
			FeatureLevel.Level_10_1,
			FeatureLevel.Level_10_0
		};
		D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, featureLevels, out ID3D11Device device, out FeatureLevel featureLevel, out ID3D11DeviceContext immediateContext).CheckError();
		_device = device;
		_context = immediateContext;
		_featureLevel = featureLevel;
	}

	private void CreateDuplication()
	{
		_duplication = _output.DuplicateOutput(_device);
		_duplicationFirstFramePending = true;
		_slotContentionLogged = false;
		_duplicationCandidateFailuresLogged.Clear();
	}

	private static bool IsDuplicationBringUpFailure(Exception ex)
	{
		if (ex is SharpGenException || ex is InvalidOperationException || ex is NotSupportedException)
		{
			return true;
		}
		return false;
	}

	private static string FormatLuid(long? luid)
	{
		if (!luid.HasValue)
		{
			return "-";
		}
		return $"0x{luid.Value:x16}";
	}

	private static string FormatBounds(Rectangle bounds)
	{
		return $"{bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}";
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

	private async Task CaptureLoop(CancellationToken cancellationToken)
	{
		int? pendingDeniedHoldHr = null;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				if (pendingDeniedHoldHr.HasValue)
				{
					int valueOrDefault = pendingDeniedHoldHr.GetValueOrDefault();
					pendingDeniedHoldHr = null;
					if (!(await AwaitDeniedHoldAsync(valueOrDefault, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
					{
						break;
					}
					continue;
				}
				if (!TryReseedForInstall(ref pendingDeniedHoldHr))
				{
					break;
				}
				if (pendingDeniedHoldHr.HasValue)
				{
					continue;
				}
				if (_duplication == null)
				{
					await Task.Delay(1000, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					if (!TryRebuildDuplicationForContention(ref pendingDeniedHoldHr))
					{
						break;
					}
				}
				else
				{
					CaptureFrame(cancellationToken);
				}
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
				Log($"duplication reset display={DisplayIndex}: {ex3.Message}", "CaptureLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 898);
				lock (_sync)
				{
					if (_disposed)
					{
						break;
					}
					_duplication?.Dispose();
					_duplication = null;
					try
					{
						EnsureDuplication();
						_lastReseedAttemptQpc = Stopwatch.GetTimestamp();
					}
					catch (Exception ex4) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex4))
					{
						Log($"duplication reset retry display={DisplayIndex}: {ex4.Message}", "CaptureLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 917);
						pendingDeniedHoldHr = CaptureAccessDeniedClassifier.LoggableHResult(ex4);
					}
					catch (Exception ex5) when (DuplicationSlotContentionClassifier.IsSlotContention(ex5))
					{
						LogSlotContentionOnce();
					}
					catch (Exception ex6)
					{
						Log($"duplication reset retry display={DisplayIndex}: {ex6.Message}", "CaptureLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 929);
						SignalCaptureFailure("duplication-recreate", ex6);
						break;
					}
				}
			}
			catch (SharpGenException ex7) when (ex7.HResult == -2005270485)
			{
				pendingDeniedHoldHr = ex7.HResult;
			}
			catch (SharpGenException ex8) when (ex8.HResult == -2005270527 || ex8.HResult == -2005270523 || ex8.HResult == -2005270521)
			{
				Log($"duplication device error display={DisplayIndex} hr=0x{ex8.HResult:X8}: {ex8.Message}", "CaptureLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 952);
				lock (_sync)
				{
					if (_disposed)
					{
						break;
					}
					_duplication?.Dispose();
					_duplication = null;
					try
					{
						EnsureDuplication();
						_lastReseedAttemptQpc = Stopwatch.GetTimestamp();
					}
					catch (Exception ex9) when (DuplicationSlotContentionClassifier.IsSlotContention(ex9))
					{
						LogSlotContentionOnce();
					}
					catch (Exception ex10)
					{
						SignalCaptureFailure("duplication-recreate", ex10);
						break;
					}
				}
			}
			catch (Exception ex11) when (DuplicationSlotContentionClassifier.IsSlotContention(ex11))
			{
				LogSlotContentionOnce();
			}
			catch (Exception ex12)
			{
				SignalCaptureFailure("capture-loop", ex12);
				break;
			}
		}
	}

	private async Task<bool> AwaitDeniedHoldAsync(int entryHResult, CancellationToken cancellationToken)
	{
		CaptureDeniedHoldResult captureDeniedHoldResult = await (_deniedHold ?? (_deniedHold = new CaptureDeniedHold(DisplayIndex, DeniedHoldCapSeconds?.Invoke(), MarkSourceUnavailable, SignalCaptureAccessDenied, RecreateDuplicationOnly))).RunAsync(entryHResult, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		switch (captureDeniedHoldResult.Outcome)
		{
		case CaptureDeniedHoldOutcome.Recovered:
			Interlocked.Exchange(ref _accessDeniedSignaled, 0);
			return true;
		case CaptureDeniedHoldOutcome.Escalated:
			SignalCaptureFailure("duplication-recreate", captureDeniedHoldResult.EscalationException);
			return false;
		default:
			return false;
		}
	}

	private void RecreateDuplicationOnly()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				throw new OperationCanceledException();
			}
			_duplication?.Dispose();
			_duplication = null;
			EnsureDuplicationOnly();
			_lastReseedAttemptQpc = Stopwatch.GetTimestamp();
		}
	}

	private bool TryReseedForInstall(ref int? pendingDeniedHoldHr)
	{
		long episodeCaptureFloorWallMs = _router.EpisodeCaptureFloorWallMs;
		if (episodeCaptureFloorWallMs <= 0)
		{
			return true;
		}
		long pendingSeedCaptureWallMs;
		lock (_frameSync)
		{
			pendingSeedCaptureWallMs = _pendingSeedCaptureWallMs;
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (!IsReseedAttemptDue(episodeCaptureFloorWallMs, pendingSeedCaptureWallMs, _reseedHandledFloorWallMs, timestamp, _lastReseedAttemptQpc, ReseedRetryIntervalQpc))
		{
			if (pendingSeedCaptureWallMs >= episodeCaptureFloorWallMs)
			{
				_reseedHandledFloorWallMs = episodeCaptureFloorWallMs;
			}
			return true;
		}
		_reseedHandledFloorWallMs = episodeCaptureFloorWallMs;
		_lastReseedAttemptQpc = timestamp;
		try
		{
			RecreateDuplicationOnly();
		}
		catch (Exception ex) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex))
		{
			KuroLog.LocalOnly("capture", $"install reseed display={DisplayIndex} backend=dxgi outcome=denied ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "TryReseedForInstall", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1118);
			pendingDeniedHoldHr = CaptureAccessDeniedClassifier.LoggableHResult(ex);
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex3) when (DuplicationSlotContentionClassifier.IsSlotContention(ex3))
		{
			KuroLog.LocalOnly("capture", $"install reseed display={DisplayIndex} backend=dxgi outcome=slot_contention ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "TryReseedForInstall", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1132);
			LogSlotContentionOnce();
			return true;
		}
		catch (Exception ex4)
		{
			SignalCaptureFailure("duplication-recreate", ex4);
			return false;
		}
		Interlocked.Exchange(ref _nextCaptureQpc, 0L);
		KuroLog.LocalOnly("capture", $"install reseed display={DisplayIndex} backend=dxgi outcome=ok ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "TryReseedForInstall", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1149);
		return true;
	}

	internal static bool IsReseedAttemptDue(long floorWallMs, long seedCaptureWallMs, long handledFloorWallMs, long nowQpc, long lastAttemptQpc, long retryIntervalQpc)
	{
		if (floorWallMs > 0 && seedCaptureWallMs < floorWallMs)
		{
			if (floorWallMs == handledFloorWallMs)
			{
				return nowQpc - lastAttemptQpc >= retryIntervalQpc;
			}
			return true;
		}
		return false;
	}

	private bool TryRebuildDuplicationForContention(ref int? pendingDeniedHoldHr)
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return false;
			}
			_lastReseedAttemptQpc = Stopwatch.GetTimestamp();
			try
			{
				EnsureDuplicationOnly();
			}
			catch (Exception ex) when (CaptureAccessDeniedClassifier.IsAccessDenied(ex))
			{
				pendingDeniedHoldHr = CaptureAccessDeniedClassifier.LoggableHResult(ex);
				return true;
			}
			catch (Exception ex2) when (DuplicationSlotContentionClassifier.IsSlotContention(ex2))
			{
				LogSlotContentionOnce();
				return true;
			}
			catch (Exception ex3)
			{
				SignalCaptureFailure("duplication-recreate", ex3);
				return false;
			}
		}
		Interlocked.Exchange(ref _nextCaptureQpc, 0L);
		return true;
	}

	private void LogSlotContentionOnce()
	{
		if (!_slotContentionLogged)
		{
			_slotContentionLogged = true;
			KuroLog.LocalOnly("capture", $"duplication slot contention display={DisplayIndex} hr=0x{-2005270494:X8}; " + "holding on the reseed hedge (third-party capture holds the output slot)", "LogSlotContentionOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1225);
		}
	}

	private void CaptureFrame(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		OutduplFrameInfo frameInfo = default;
		IDXGIResource desktopResource = null;
		bool flag = false;
		try
		{
			Result result = _duplication.AcquireNextFrame(100u, out frameInfo, out desktopResource);
			if (result.Code == -2005270489)
			{
				_instrument.RecordAcquireNone(Stopwatch.GetTimestamp());
				return;
			}
			result.CheckError();
			flag = true;
			if (desktopResource == null)
			{
				return;
			}
			UpdatePointerState(frameInfo);
			DesktopDuplicationFrameUpdate desktopDuplicationFrameUpdate = DesktopDuplicationFrameClassifier.Classify(frameInfo.LastPresentTime, frameInfo.AccumulatedFrames, frameInfo.LastMouseUpdateTime);
			if (!desktopDuplicationFrameUpdate.HasDesktopUpdate)
			{
				if (desktopDuplicationFrameUpdate.HasPointerUpdate)
				{
					PublishCursorOnlyFrame();
					_instrument.RecordAcquireCursorOnly();
				}
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			_instrument.RecordAcquireDesktopUpdate(frameInfo.AccumulatedFrames, timestamp);
			bool duplicationFirstFramePending = _duplicationFirstFramePending;
			_duplicationFirstFramePending = false;
			(long SourceQpc, TimestampKind Kind, TimestampQuality Quality) tuple = SelectDesktopUpdateStamp(duplicationFirstFramePending, frameInfo.LastPresentTime, timestamp);
			long item = tuple.SourceQpc;
			TimestampKind item2 = tuple.Kind;
			TimestampQuality item3 = tuple.Quality;
			long num = RecordingClock.Shared.WallMsFromQpc(timestamp);
			RecorderWriteRouter? router = _router;
			if (router == null || !router.CanAcceptFrame(RecordingClock.Shared.WallMsFromQpc(item), num))
			{
				_instrument.RecordRouterRejectPreReadback();
				return;
			}
			if (ShouldSkipExpensiveReadback(item))
			{
				_instrument.RecordGateSkip();
				return;
			}
			using ID3D11Texture2D iD3D11Texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();
			if (!TextureMatchesOutput(iD3D11Texture2D))
			{
				SignalCaptureFailure("duplication-size-change", new InvalidOperationException($"DXGI texture size changed for display={DisplayIndex}; segment restart required."));
				return;
			}
			long timestamp2 = Stopwatch.GetTimestamp();
			_context.CopyResource(_stagingTexture, iD3D11Texture2D);
			long timestamp3 = Stopwatch.GetTimestamp();
			_context.Map(_stagingTexture, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mappedResource).CheckError();
			long timestamp4 = Stopwatch.GetTimestamp();
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
				_context.Unmap(_stagingTexture, 0u);
			}
			_instrument.RecordReadbackPhases(timestamp3 - timestamp2, timestamp4 - timestamp3, Stopwatch.GetTimestamp() - timestamp4);
			PublishCapturedFrame(item, num, item2, item3);
		}
		finally
		{
			desktopResource?.Dispose();
			if (flag)
			{
				try
				{
					_duplication.ReleaseFrame();
				}
				catch
				{
				}
			}
		}
	}

	internal static (long SourceQpc, TimestampKind Kind, TimestampQuality Quality) SelectDesktopUpdateStamp(bool firstFrameAfterCreate, long lastPresentTimeQpc, long acquisitionQpc)
	{
		if (firstFrameAfterCreate || lastPresentTimeQpc == 0L)
		{
			return (SourceQpc: acquisitionQpc, Kind: TimestampKind.AcquisitionQpc, Quality: (!firstFrameAfterCreate) ? TimestampQuality.Degraded : TimestampQuality.Authoritative);
		}
		return (SourceQpc: lastPresentTimeQpc, Kind: TimestampKind.DxgiLastPresentTime, Quality: TimestampQuality.Authoritative);
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

	private bool TextureMatchesOutput(ID3D11Texture2D texture)
	{
		Texture2DDescription description = texture.Description;
		if (description.Width == (uint)_sourceWidth)
		{
			return description.Height == (uint)_sourceHeight;
		}
		return false;
	}

	private void DisposeDuplication()
	{
		_duplication?.Dispose();
		_duplication = null;
		_stagingTexture?.Dispose();
		_stagingTexture = null;
		_context?.Dispose();
		_context = null;
		_device?.Dispose();
		_device = null;
		_output?.Dispose();
		_output = null;
		_adapter?.Dispose();
		_adapter = null;
		_factory?.Dispose();
		_factory = null;
		_hasPointerShape = false;
		_pointerVisible = false;
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
			_captureTaskAbandoned = true;
			KuroLog.LocalOnly("capture", $"dxgi dispose entry lock wedged display={DisplayIndex}: entering the bounded cleanup regime", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1520);
			_captureCts?.Cancel();
			_disposed = true;
		}
		RecorderWriteRouter router = _router;
		if (router != null)
		{
			router.GateClosed -= InvalidateMailboxForGateClose;
		}
		try
		{
			Task? captureTask = _captureTask;
			if (captureTask != null && !captureTask.Wait(1000))
			{
				_captureTaskAbandoned = true;
				Log($"capture task stop timed out display={DisplayIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1547);
			}
		}
		catch
		{
		}
		try
		{
			Task? encodeTask = _encodeTask;
			if (encodeTask != null && !encodeTask.Wait(1000))
			{
				Log($"encode task stop timed out display={DisplayIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1561);
			}
		}
		catch
		{
		}
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
		Log($"capture summary display={DisplayIndex}: produced={_producedFrameCount} encoded={_encodedFrameCount}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1582);
		_cursorRenderer.Dispose();
	}

	internal CpuCaptureInstrumentation.PipelineWindow OpenPipelineWindow()
	{
		return _instrument.OpenWindow(Stopwatch.GetTimestamp());
	}

	internal void LogPipelineSummary(CpuCaptureInstrumentation.PipelineWindow window)
	{
		_instrument.FlushWindow(window, Stopwatch.GetTimestamp(), DisplayIndex, (string m) =>
		{
			Log(m, "LogPipelineSummary", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1601);
		});
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Capture(message, memberName, filePath, lineNumber);
	}

	private void PublishCapturedFrame(long sourceQpc, long seedCaptureWallMs, TimestampKind timestampKind, TimestampQuality timestampQuality)
	{
		ulong num = FrameVisualFingerprint.ComputeSampledHash(_captureBuffer, _outputWidth, _outputHeight, _outputStride);
		int? pendingFrameChangeScore = (_hasSourceFrameVisualHash ? new int?(FrameVisualFingerprint.HammingDistance(_lastSourceFrameVisualHash, num)) : ((int?)null));
		long pendingFrameWallMs = RecordingClock.Shared.WallMsFromQpc(sourceQpc);
		long timestamp = Stopwatch.GetTimestamp();
		lock (_frameSync)
		{
			long num2 = _router?.EpisodeCaptureFloorWallMs ?? 0;
			if (num2 <= 0 || seedCaptureWallMs < num2)
			{
				return;
			}
			_instrument.RecordPublish(_hasCapturedFrame, Stopwatch.GetTimestamp() - timestamp);
			_sourceUnavailable = false;
			byte[] latestBuffer = _latestBuffer;
			byte[] captureBuffer = _captureBuffer;
			_captureBuffer = latestBuffer;
			_latestBuffer = captureBuffer;
			_hasCapturedFrame = true;
			_hasFirstFrame = true;
			_pendingFrameTimestampTicks = sourceQpc;
			_pendingFrameWallMs = pendingFrameWallMs;
			_pendingTimestampKind = timestampKind;
			_pendingTimestampQuality = timestampQuality;
			_pendingFrameKind = CaptureFrameKind.Source;
			_pendingFrameVisualHash = num;
			_pendingFrameChangeScore = pendingFrameChangeScore;
			_pendingSeedCaptureWallMs = seedCaptureWallMs;
			_sourcePublishSeq++;
			_lastSourceFrameVisualHash = num;
			_hasSourceFrameVisualHash = true;
			_producedFrameCount++;
		}
		CaptureHealthRegistry.RecordFirstFrame(_displayKey, "dxgi");
	}

	private void PublishCursorOnlyFrame()
	{
		lock (_frameSync)
		{
			if (_hasFirstFrame)
			{
				_instrument.RecordCursorOnlyPublish(clobberedPendingSourceFrame: false);
				_sourceUnavailable = false;
				_hasCursorOnlyUpdate = true;
				_cursorOnlyUpdateQpc = Stopwatch.GetTimestamp();
				_cursorOnlyPublishSeq++;
			}
		}
	}

	private bool ShouldSkipExpensiveReadback(long sourceQpc)
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

	private bool TryPrepareForCommit(PadCommitKind kind, long nowQpc, out PreparedCommit prepared, out bool skipCursorBlend)
	{
		skipCursorBlend = false;
		switch (kind)
		{
		case PadCommitKind.PendingFrame:
			return TryPrepareMailboxFrame(out prepared);
		case PadCommitKind.BlendedPad:
			return TryPrepareBlendedPad(nowQpc, out prepared);
		case PadCommitKind.PixelTrueHeartbeat:
			if (_encodeBufferHoldsLastCommit)
			{
				skipCursorBlend = true;
				return TryPrepareHeartbeatResubmit(nowQpc, out prepared);
			}
			return TryPrepareBlendedPad(nowQpc, out prepared);
		default:
			prepared = default;
			return false;
		}
	}

	private bool TryPrepareMailboxFrame(out PreparedCommit prepared)
	{
		long timestamp = Stopwatch.GetTimestamp();
		lock (_frameSync)
		{
			long timestamp2 = Stopwatch.GetTimestamp();
			if (!_hasCapturedFrame && !_hasCursorOnlyUpdate)
			{
				prepared = default;
				return false;
			}
			long timestamp3 = Stopwatch.GetTimestamp();
			Buffer.BlockCopy(_latestBuffer, 0, _encodeBuffer, 0, _latestBuffer.Length);
			_instrument.RecordConsume(freshFrame: true, timestamp2 - timestamp, Stopwatch.GetTimestamp() - timestamp3);
			PointerSnapshot pointerSnapshot = PointerSnapshot.Create(_pointerVisible, _hasPointerShape, _pointerPosition, _pointerShapeInfo, _pointerShapeBuffer);
			if (_hasCapturedFrame)
			{
				prepared = new PreparedCommit(pointerSnapshot, _pendingFrameTimestampTicks, _pendingFrameWallMs, _pendingTimestampKind, _pendingTimestampQuality, _pendingFrameKind, _pendingFrameVisualHash, _pendingFrameChangeScore, _pendingSeedCaptureWallMs, ConsumedMailbox: true, _sourcePublishSeq, _cursorOnlyPublishSeq);
			}
			else
			{
				prepared = new PreparedCommit(pointerSnapshot, _cursorOnlyUpdateQpc, RecordingClock.Shared.WallMsFromQpc(_cursorOnlyUpdateQpc), TimestampKind.AcquisitionQpc, TimestampQuality.Degraded, CaptureFrameKind.CursorOnly, _lastSourceFrameVisualHash, 0, _pendingSeedCaptureWallMs, ConsumedMailbox: true, _sourcePublishSeq, _cursorOnlyPublishSeq);
			}
			return true;
		}
	}

	private bool TryPrepareBlendedPad(long nowQpc, out PreparedCommit prepared)
	{
		long timestamp = Stopwatch.GetTimestamp();
		lock (_frameSync)
		{
			long timestamp2 = Stopwatch.GetTimestamp();
			if (!_hasFirstFrame || _sourceUnavailable)
			{
				prepared = default;
				return false;
			}
			long timestamp3 = Stopwatch.GetTimestamp();
			Buffer.BlockCopy(_latestBuffer, 0, _encodeBuffer, 0, _latestBuffer.Length);
			_instrument.RecordConsume(freshFrame: false, timestamp2 - timestamp, Stopwatch.GetTimestamp() - timestamp3);
			PadFrameStamp padFrameStamp = PadCommitPolicy.CreatePadStamp(nowQpc);
			prepared = new PreparedCommit(default, padFrameStamp.TimestampTicks, padFrameStamp.WallMs, padFrameStamp.TimestampKind, padFrameStamp.TimestampQuality, padFrameStamp.FrameKind, _lastSourceFrameVisualHash, 0, _pendingSeedCaptureWallMs, ConsumedMailbox: false, 0L, 0L);
			return true;
		}
	}

	private bool TryPrepareHeartbeatResubmit(long nowQpc, out PreparedCommit prepared)
	{
		lock (_frameSync)
		{
			if (!_hasFirstFrame || _sourceUnavailable)
			{
				prepared = default;
				return false;
			}
			PadFrameStamp padFrameStamp = PadCommitPolicy.CreatePadStamp(nowQpc);
			prepared = new PreparedCommit(default, padFrameStamp.TimestampTicks, padFrameStamp.WallMs, padFrameStamp.TimestampKind, padFrameStamp.TimestampQuality, padFrameStamp.FrameKind, _lastSourceFrameVisualHash, 0, _pendingSeedCaptureWallMs, ConsumedMailbox: false, 0L, 0L);
			return true;
		}
	}

	private void ReleaseConsumedMailbox(in PreparedCommit prepared)
	{
		lock (_frameSync)
		{
			if (_sourcePublishSeq == prepared.SourceSeqSnapshot)
			{
				_hasCapturedFrame = false;
			}
			if (_cursorOnlyPublishSeq == prepared.CursorOnlySeqSnapshot)
			{
				_hasCursorOnlyUpdate = false;
			}
		}
	}

	private void InvalidateMailboxForGateClose()
	{
		lock (_frameSync)
		{
			_hasCapturedFrame = false;
			_hasCursorOnlyUpdate = false;
			_hasFirstFrame = false;
			_pendingSeedCaptureWallMs = 0L;
			_sourcePublishSeq++;
			_cursorOnlyPublishSeq++;
			Array.Clear(_latestBuffer);
		}
	}

	private void LogPointerDebug(string message)
	{
		if (AppConfig.CaptureDebugLoggingEnabled)
		{
			long timestamp = Stopwatch.GetTimestamp();
			if (!(message == _lastPointerLogMessage) || timestamp - _lastPointerLogTicks >= Stopwatch.Frequency * 2)
			{
				_lastPointerLogTicks = timestamp;
				_lastPointerLogMessage = message;
				Log(message, "LogPointerDebug", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 1972);
			}
		}
	}

	private unsafe void UpdatePointerState(OutduplFrameInfo frameInfo)
	{
		byte[] array = null;
		OutduplPointerShapeInfo pointerShapeInfo = default;
		bool flag = false;
		if (frameInfo.PointerShapeBufferSize != 0)
		{
			array = new byte[frameInfo.PointerShapeBufferSize];
			Result framePointerShape;
			fixed (byte* pointerShapeBuffer = array)
			{
				framePointerShape = _duplication.GetFramePointerShape((uint)array.Length, (nint)pointerShapeBuffer, out var pointerShapeBufferSizeRequired, out pointerShapeInfo);
				if (framePointerShape.Code == -2005270525 || (framePointerShape.Success && pointerShapeBufferSizeRequired > array.Length))
				{
					array = new byte[pointerShapeBufferSizeRequired];
					fixed (byte* pointerShapeBuffer2 = array)
					{
						framePointerShape = _duplication.GetFramePointerShape((uint)array.Length, (nint)pointerShapeBuffer2, out var _, out pointerShapeInfo);
					}
				}
			}
			flag = framePointerShape.Success;
			LogPointerDebug(flag ? $"pointer shape display={DisplayIndex}: type={(PointerShapeType)pointerShapeInfo.Type} size={pointerShapeInfo.Width}x{pointerShapeInfo.Height} pitch={pointerShapeInfo.Pitch} buffer={frameInfo.PointerShapeBufferSize}" : $"pointer shape read failed display={DisplayIndex} hr=0x{framePointerShape.Code:X8}");
		}
		lock (_frameSync)
		{
			if (flag && array != null)
			{
				_pointerShapeBuffer = array;
				_pointerShapeInfo = pointerShapeInfo;
				_hasPointerShape = true;
			}
			if (frameInfo.LastMouseUpdateTime != 0L)
			{
				_pointerVisible = frameInfo.PointerPosition.Visible;
				_pointerPosition = new Point(frameInfo.PointerPosition.Position.X, frameInfo.PointerPosition.Position.Y);
			}
		}
		if (frameInfo.LastMouseUpdateTime != 0L)
		{
			LogPointerDebug($"pointer pos display={DisplayIndex}: visible={_pointerVisible} pos={_pointerPosition.X},{_pointerPosition.Y} bounds={_outputBounds.Left},{_outputBounds.Top},{_outputBounds.Right},{_outputBounds.Bottom}");
		}
	}

	private bool TryOverlayDuplicatedPointer(byte[] destination, PointerSnapshot snapshot, in PadCursorObservation cursor, out CursorFrameSample sample, out string reason)
	{
		sample = CursorFrameSample.None;
		if (_sourceBuffer != null)
		{
			reason = "scaled-output";
			return false;
		}
		if (!snapshot.Visible || !snapshot.HasShape)
		{
			reason = $"not-ready visible={snapshot.Visible} hasShape={snapshot.HasShape}";
			return false;
		}
		PointerShapeType type = (PointerShapeType)snapshot.ShapeInfo.Type;
		if (type != PointerShapeType.Color)
		{
			reason = $"unsupported-shape type={type}";
			return false;
		}
		int width = (int)snapshot.ShapeInfo.Width;
		int height = (int)snapshot.ShapeInfo.Height;
		int pitch = (int)snapshot.ShapeInfo.Pitch;
		if (width <= 0 || height <= 0 || pitch <= 0)
		{
			reason = $"invalid-shape size={width}x{height} pitch={pitch}";
			return false;
		}
		int num = Math.Max(snapshot.Position.X, _outputBounds.Left);
		int num2 = Math.Max(snapshot.Position.Y, _outputBounds.Top);
		int num3 = Math.Min(snapshot.Position.X + width, _outputBounds.Right);
		int num4 = Math.Min(snapshot.Position.Y + height, _outputBounds.Bottom);
		if (num >= num3 || num2 >= num4)
		{
			reason = $"offscreen pos={snapshot.Position.X},{snapshot.Position.Y} size={width}x{height}";
			return false;
		}
		int sourceStartX = num - snapshot.Position.X;
		int sourceStartY = num2 - snapshot.Position.Y;
		int num5 = num - _outputBounds.Left;
		int num6 = num2 - _outputBounds.Top;
		int copyWidth = num3 - num;
		int copyHeight = num4 - num2;
		BlendColorPointer(destination, snapshot.ShapeBuffer, num5, num6, sourceStartX, sourceStartY, copyWidth, copyHeight, pitch);
		sample = CursorFrameSample.FromDuplicatedPointer(new Point(snapshot.Position.X + snapshot.ShapeInfo.HotSpot.X, snapshot.Position.Y + snapshot.ShapeInfo.HotSpot.Y), in cursor);
		reason = $"blended type={type} pos={snapshot.Position.X},{snapshot.Position.Y} size={width}x{height} dest={num5},{num6}";
		return true;
	}

	private unsafe void BlendColorPointer(byte[] destination, byte[] pointerShapeBuffer, int destStartX, int destStartY, int sourceStartX, int sourceStartY, int copyWidth, int copyHeight, int pointerPitch)
	{
		fixed (byte* ptr = destination)
		{
			fixed (byte* ptr2 = pointerShapeBuffer)
			{
				for (int i = 0; i < copyHeight; i++)
				{
					byte* ptr3 = ptr2 + (sourceStartY + i) * pointerPitch + sourceStartX * 4;
					byte* ptr4 = ptr + (destStartY + i) * _outputStride + destStartX * 4;
					for (int j = 0; j < copyWidth; j++)
					{
						byte* ptr5 = ptr3 + j * 4;
						byte* ptr6 = ptr4 + j * 4;
						byte b = ptr5[3];
						switch (b)
						{
						case byte.MaxValue:
							*ptr6 = *ptr5;
							ptr6[1] = ptr5[1];
							ptr6[2] = ptr5[2];
							ptr6[3] = byte.MaxValue;
							break;
						default:
						{
							int num = 255 - b;
							*ptr6 = (byte)((*ptr5 * b + *ptr6 * num) / 255);
							ptr6[1] = (byte)((ptr5[1] * b + ptr6[1] * num) / 255);
							ptr6[2] = (byte)((ptr5[2] * b + ptr6[2] * num) / 255);
							ptr6[3] = byte.MaxValue;
							break;
						}
						case 0:
							break;
						}
					}
				}
			}
		}
	}

	private bool HasPendingFrameForCadence(out bool hasFirstFrame)
	{
		lock (_frameSync)
		{
			hasFirstFrame = _hasFirstFrame;
			return _hasCapturedFrame || _hasCursorOnlyUpdate;
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

	private PadCursorObservation SampleCursorForPad(long nowQpc)
	{
		long wallMs = RecordingClock.Shared.WallMsFromQpc(nowQpc);
		MousePositionEntry mousePositionEntry = _frameState?.MousePositionForFrame(wallMs);
		Point point = ((mousePositionEntry == null) ? Cursor.Position : new Point(mousePositionEntry.X, mousePositionEntry.Y));
		return PadCursorObservation.WithSystemSprite(point.X, point.Y);
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
				_instrument.MaybeFlushDetail(timestamp, DisplayIndex, (string m) =>
				{
					Log(m, "EncodeLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 2251);
				});
				if (_router.GateOpenElapsedMsOrNegative() < 0)
				{
					continue;
				}
				bool hasPendingFrame = HasPendingFrameForCadence(out var hasFirstFrame);
				PadCursorObservation cursor = SampleCursorForPad(timestamp);
				CaptureCadenceDecision captureCadenceDecision = _cadence.OnWake(timestamp, hasFirstFrame, hasPendingFrame, HasRecentActivityForCadence(timestamp), cursor);
				if (captureCadenceDecision.Reanchored && captureCadenceDecision.LateMs > 2000 && timestamp - _lastPacingStallLogTicks >= Stopwatch.Frequency * 30)
				{
					_lastPacingStallLogTicks = timestamp;
					KuroLog.LocalOnly("capture", $"encode pacing re-anchored display={DisplayIndex} late_ms={captureCadenceDecision.LateMs}", "EncodeLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 2275);
				}
				if (!captureCadenceDecision.ShouldCommit || !TryPrepareForCommit(captureCadenceDecision.Kind, timestamp, out var prepared, out var skipCursorBlend))
				{
					continue;
				}
				if (!skipCursorBlend)
				{
					_encodeBufferHoldsLastCommit = false;
				}
				long wallMs = prepared.WallMs;
				if (!_router.CanAcceptFrame(wallMs, prepared.SeedCaptureWallMs))
				{
					if (prepared.ConsumedMailbox)
					{
						ReleaseConsumedMailbox(in prepared);
						_router.NoteFrameDropped(DisplayIndex);
					}
					_instrument.RecordRejectAfterConsume(atCommit: false, prepared.FrameKind == CaptureFrameKind.Source);
					continue;
				}
				CursorFrameSample sample = CursorFrameSample.None;
				string reason;
				if (skipCursorBlend)
				{
					sample = _lastCommittedCursorSample;
				}
				else if (!TryOverlayDuplicatedPointer(_encodeBuffer, prepared.PointerSnapshot, in cursor, out sample, out reason))
				{
					MousePositionEntry mousePositionEntry = _frameState?.MousePositionForFrame(wallMs);
					Point hotspot = ((mousePositionEntry == null) ? Cursor.Position : new Point(mousePositionEntry.X, mousePositionEntry.Y));
					sample = CursorFrameSample.FromSystemCursor(hotspot, blended: false, in cursor);
					if (!_cursorRenderer.TryOverlayCursor(_encodeBuffer, _outputBounds, _outputStride, hotspot, in cursor, out sample, out string reason2))
					{
						LogPointerDebug($"cursor overlay skipped display={DisplayIndex}: dxgi={reason}; fallback={reason2}");
						string text = reason2.Split(' ')[0];
						bool flag;
						switch (text)
						{
						case "no-cursor-info":
						case "copy-icon-failed":
						case "get-icon-info-failed":
						case "draw-icon-failed":
						case "invalid-icon-size":
						case "renderer-disposed":
							flag = true;
							break;
						default:
							flag = false;
							break;
						}
						if (flag && _loggedOverlayFailureCategories.Add(text))
						{
							Log($"dxgi cursor overlay unavailable display={DisplayIndex}: dxgi={reason}; fallback={reason2}", "EncodeLoop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 2350);
						}
					}
					else
					{
						LogPointerDebug($"cursor overlay fallback display={DisplayIndex}: dxgi={reason}; fallback={reason2}");
					}
				}
				else
				{
					LogPointerDebug($"cursor overlay dxgi display={DisplayIndex}: {reason}");
				}
				int cursorScreen = (sample.HasCursor ? MonitorTable.Current.IndexOfNearest(sample.Hotspot.X, sample.Hotspot.Y) : 0);
				_inCommit = true;
				bool flag2;
				try
				{
					flag2 = _router.CommitFrame(new FrameCommitRequest(DisplayIndex, _encodeBuffer, prepared.TimestampTicks, wallMs, prepared.SeedCaptureWallMs, prepared.TimestampKind, prepared.TimestampQuality, prepared.FrameKind, CaptureBackendKind.DesktopDuplication, prepared.VisualHash, prepared.ChangeScore, sample, cursorScreen)
					{
						CursorBlendChanged = (captureCadenceDecision.CursorBlendChanged ? new bool?(true) : ((bool?)null))
					});
				}
				finally
				{
					_inCommit = false;
				}
				if (prepared.ConsumedMailbox)
				{
					ReleaseConsumedMailbox(in prepared);
				}
				if (flag2)
				{
					_encodeBufferHoldsLastCommit = true;
					_lastCommittedCursorSample = sample;
					_cadence.MarkCommitted(timestamp);
					_encodedFrameCount++;
					_instrument.RecordCommit();
				}
				else
				{
					_instrument.RecordRejectAfterConsume(atCommit: true, prepared.FrameKind == CaptureFrameKind.Source);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				SignalCaptureFailure("encode-loop", ex2);
				break;
			}
		}
	}

	private void SignalCaptureAccessDenied(int hresult)
	{
		if (Interlocked.Exchange(ref _accessDeniedSignaled, 1) != 0)
		{
			return;
		}
		Log($"display access denied display={DisplayIndex} hr=0x{hresult:X8} remote_session={RemoteSessionProbe.LogValue}: holding source and retrying duplication", "SignalCaptureAccessDenied", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 2441);
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
			_hasCursorOnlyUpdate = false;
		}
	}

	private void OnPipelineStall(CapturePipelineStallException stall)
	{
		if (stall.Stage == "commit_wedge")
		{
			SignalCaptureFailure("dxgi-pipeline-stall", new CapturePipelineStallException(DisplayIndex, stall.SilentSeconds, stall.Stage, $"encode loop produced no heartbeat for >{25}s (wedged native call) backend=dxgi display={DisplayIndex} in_commit={(_inCommit ? "true" : "false")}"));
		}
		else
		{
			SignalCaptureFailure("dxgi-watchdog", stall);
		}
	}

	private void SignalCaptureFailure(string source, Exception ex)
	{
		if (Interlocked.Increment(ref _frameArrivedErrors) <= 10)
		{
			Log($"{source} failure display={DisplayIndex}: {KuroLog.ExceptionSummary(ex)}", "SignalCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DisplayCapture.cs", 2483);
		}
		if (Interlocked.Exchange(ref _failureRaised, 1) != 0)
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

	private static int NormalizeDimension(int value)
	{
		return Math.Max(value & -2, 2);
	}
}
