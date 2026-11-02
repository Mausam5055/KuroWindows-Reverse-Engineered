using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using Vortice.DXGI;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace KuroWindows.Capture.Gen2;

internal sealed class WgcGen2FrameSource : IGen2FrameSource, IDisposable
{
	private readonly IGen2FrameSink _sink;

	private readonly Screen _screen;

	private readonly int _displayIndex;

	private readonly int _outputWidth;

	private readonly int _outputHeight;

	private readonly IDirect3DDevice _winrtDevice;

	private readonly WgcBorderMode _borderMode;

	private readonly string? _sessionBorderBlocker;

	private readonly object _processGate = new object();

	private volatile bool _teardown;

	private volatile bool _gateAbandoned;

	private GraphicsCaptureItem? _item;

	private Direct3D11CaptureFramePool? _framePool;

	private GraphicsCaptureSession? _session;

	private SizeInt32 _currentContentSize;

	private volatile bool _disposed;

	public string FrontEndName => "wgc_gpu";

	public string RuntimeFailureKey => CapturePath.PathName;

	public CaptureBackendKind BackendKind => CaptureBackendKind.WindowsGraphicsCapture;

	public CapturePathSelection CapturePath => CapturePathSelection.Wgc(_borderMode, _sessionBorderBlocker, "gpu_v2_wgc");

	public WgcGen2FrameSource(IGen2FrameSink sink, GpuEncoderContext gpu, Screen screen, int displayIndex, int outputWidth, int outputHeight, WgcBorderMode borderMode = WgcBorderMode.RequireSuppressedBorder)
	{
		_sink = sink;
		_screen = screen;
		_displayIndex = displayIndex;
		_outputWidth = outputWidth;
		_outputHeight = outputHeight;
		_borderMode = borderMode;
		_sessionBorderBlocker = WgcMonitorCaptureBackend.BorderSuppressionBlocker;
		using IDXGIDevice iDXGIDevice = gpu.Device.QueryInterface<IDXGIDevice>();
		_winrtDevice = WgcInterop.CreateWinRtDevice(iDXGIDevice.NativePointer);
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!(_session != null))
		{
			lock (_processGate)
			{
				_teardown = false;
			}
			if (!GraphicsCaptureSession.IsSupported())
			{
				_sink.SourceAccessDenied();
				throw new NotSupportedException("Windows Graphics Capture is not supported on this Windows build.");
			}
			string sessionBorderBlocker = _sessionBorderBlocker;
			if (sessionBorderBlocker != null && _borderMode == WgcBorderMode.RequireSuppressedBorder)
			{
				throw new WgcBorderUnavailableException(sessionBorderBlocker, "gpu_v2 refused: capture border cannot be suppressed (" + sessionBorderBlocker + ").");
			}
			_item = WgcInterop.CreateItemForMonitor(_screen);
			_currentContentSize = _item.Size;
			int num = _currentContentSize.Width & -2;
			int num2 = _currentContentSize.Height & -2;
			if (num != _outputWidth || num2 != _outputHeight)
			{
				DisposeCaptureObjects();
				throw new InvalidOperationException($"gpu_v2 content size {num}x{num2} does not match output {_outputWidth}x{_outputHeight}.");
			}
			_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _currentContentSize);
			_framePool.FrameArrived += OnFrameArrived;
			_item.Closed += OnItemClosed;
			_session = _framePool.CreateCaptureSession(_item);
			try
			{
				_session.IsCursorCaptureEnabled = false;
			}
			catch (Exception ex)
			{
				KuroLog.Capture($"gpu_v2 WGC cursor capture toggle unsupported display={_displayIndex}: {ex.GetType().Name}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 153);
			}
			bool flag = _borderMode == WgcBorderMode.RequireSuppressedBorder;
			if (flag)
			{
				_session.IsBorderRequired = false;
			}
			else
			{
				KuroLog.Capture($"WARNING gpu_v2 WGC visible capture border enabled display={_displayIndex} reason={_sessionBorderBlocker ?? "visible_border_requested"}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 162);
			}
			KuroLog.Capture($"gpu_v2 WGC session created display={_displayIndex} border_suppressed={flag.ToString().ToLowerInvariant()} border_visible={(!flag).ToString().ToLowerInvariant()} cursor_capture_disabled=true", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 166);
			_session.StartCapture();
		}
	}

	public Task StopAsync()
	{
		DisposeCaptureObjects();
		return Task.CompletedTask;
	}

	public void RequestReseed()
	{
		long timestamp = Stopwatch.GetTimestamp();
		GraphicsCaptureSession session;
		Direct3D11CaptureFramePool framePool;
		GraphicsCaptureItem item;
		lock (_processGate)
		{
			if (_disposed || _teardown || _framePool == null || _item == null || _session == null)
			{
				return;
			}
			session = _session;
			_session = null;
			framePool = _framePool;
			item = _item;
		}
		GraphicsCaptureSession graphicsCaptureSession = null;
		try
		{
			session.Dispose();
			graphicsCaptureSession = framePool.CreateCaptureSession(item);
			try
			{
				graphicsCaptureSession.IsCursorCaptureEnabled = false;
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("capture", $"gpu_v2 WGC reseed cursor capture toggle unsupported display={_displayIndex}: {ex.GetType().Name}", "RequestReseed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 237);
			}
			if (_borderMode == WgcBorderMode.RequireSuppressedBorder)
			{
				graphicsCaptureSession.IsBorderRequired = false;
			}
			graphicsCaptureSession.StartCapture();
		}
		catch (Exception ex2)
		{
			try
			{
				graphicsCaptureSession?.Dispose();
			}
			catch
			{
			}
			bool flag;
			lock (_processGate)
			{
				flag = _disposed || _teardown || (object)_framePool != framePool;
			}
			if (flag)
			{
				KuroLog.LocalOnly("capture", $"gpu_v2 WGC reseed display={_displayIndex} outcome=skipped_torn_down ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "RequestReseed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 263);
			}
			else
			{
				KuroLog.LocalOnly("capture", $"gpu_v2 WGC reseed display={_displayIndex} outcome={ex2.GetType().Name} ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "RequestReseed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 270);
				_sink.SourceFailed("gpu-v2-reseed-session", ex2);
			}
			return;
		}
		bool flag2 = false;
		lock (_processGate)
		{
			if (!_disposed && !_teardown && (object)_framePool == framePool && _session == null)
			{
				_session = graphicsCaptureSession;
				flag2 = true;
			}
		}
		if (!flag2)
		{
			try
			{
				graphicsCaptureSession.Dispose();
			}
			catch
			{
			}
			KuroLog.LocalOnly("capture", $"gpu_v2 WGC reseed display={_displayIndex} outcome=skipped_torn_down ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "RequestReseed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 297);
		}
		else
		{
			KuroLog.LocalOnly("capture", $"gpu_v2 WGC reseed display={_displayIndex} outcome=ok ms={(Stopwatch.GetTimestamp() - timestamp) * 1000 / Stopwatch.Frequency}", "RequestReseed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 304);
		}
	}

	private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
	{
		try
		{
			while (true)
			{
				lock (_processGate)
				{
					if (_disposed || _teardown)
					{
						break;
					}
					Direct3D11CaptureFrame direct3D11CaptureFrame = sender.TryGetNextFrame();
					if ((object)direct3D11CaptureFrame == null)
					{
						break;
					}
					using (direct3D11CaptureFrame)
					{
						ProcessFrame(direct3D11CaptureFrame);
					}
				}
			}
		}
		catch (Exception ex)
		{
			_sink.SourceFailed("gpu-v2-frame-arrived", ex);
		}
	}

	private void ProcessFrame(Direct3D11CaptureFrame frame)
	{
		if (_disposed)
		{
			return;
		}
		SizeInt32 contentSize = frame.ContentSize;
		if (contentSize.Width != _currentContentSize.Width || contentSize.Height != _currentContentSize.Height)
		{
			_currentContentSize = contentSize;
			if ((contentSize.Width & -2) != _outputWidth || (contentSize.Height & -2) != _outputHeight)
			{
				_sink.SourceFailed("gpu-v2-size-change", new InvalidOperationException($"gpu_v2 content size changed to {contentSize.Width}x{contentSize.Height}; segment restart required."));
				return;
			}
			try
			{
				_framePool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _currentContentSize);
			}
			catch (Exception ex)
			{
				_sink.SourceFailed("gpu-v2-frame-pool-recreate", ex);
				return;
			}
		}
		long num = TimeSpanToQpc(frame.SystemRelativeTime);
		_sink.RecordAcquireOutcome(Gen2AcquireOutcome.DesktopUpdate, 1L);
		if (!_sink.ShouldDeliverFrame(num, num))
		{
			return;
		}
		using ID3D11Texture2D iD3D11Texture2D = WgcInterop.GetTextureFromSurface(frame.Surface);
		Texture2DDescription description = iD3D11Texture2D.Description;
		if ((int)description.Width < _outputWidth || (int)description.Height < _outputHeight)
		{
			_sink.SourceFailed("gpu-v2-texture-size", new InvalidOperationException($"gpu_v2 texture {description.Width}x{description.Height} is smaller than output {_outputWidth}x{_outputHeight}."));
		}
		else
		{
			_sink.DeliverFrame(iD3D11Texture2D, num, num, TimestampKind.WgcSystemRelativeTime, TimestampQuality.Authoritative);
		}
	}

	private void OnItemClosed(GraphicsCaptureItem sender, object args)
	{
		_sink.SourceFailed("gpu-v2-item-closed", new InvalidOperationException("gpu_v2 capture item closed."));
	}

	private void DisposeCaptureObjects()
	{
		if (_gateAbandoned)
		{
			_teardown = true;
		}
		else if (Monitor.TryEnter(_processGate, TimeSpan.FromSeconds(2.0)))
		{
			try
			{
				_teardown = true;
			}
			finally
			{
				Monitor.Exit(_processGate);
			}
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
		else
		{
			_gateAbandoned = true;
			_teardown = true;
			KuroLog.LocalOnly("capture", $"gpu_v2 WGC teardown gate wedged display={_displayIndex}: leaking capture objects to stay bounded", "DisposeCaptureObjects", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\WgcGen2FrameSource.cs", 453);
			_session = null;
			_framePool = null;
			_item = null;
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			DisposeCaptureObjects();
		}
	}

	private static long TimeSpanToQpc(TimeSpan relativeTime)
	{
		return (long)Math.Round((double)relativeTime.Ticks * ((double)Stopwatch.Frequency / 10000000.0));
	}
}
