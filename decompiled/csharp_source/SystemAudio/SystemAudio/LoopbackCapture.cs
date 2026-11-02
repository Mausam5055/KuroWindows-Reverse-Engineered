using System;
using System.Threading;

namespace SystemAudio;

internal sealed class LoopbackCapture
{
	internal sealed record PendingInterruption(string Reason, int Hr);

	private const uint CaptureWaitTimeoutMs = 200u;

	private const uint EndpointReprobeMs = 5000u;

	private const uint ReopenRetryMs = 500u;

	private readonly AudioEndpoint _endpoint;

	private readonly SampleRing _ring;

	private readonly AudioClock _clock;

	private readonly CaptureCounters _counters;

	private readonly string _threadName;

	private readonly ManualResetEventSlim _firstProbe = new ManualResetEventSlim(initialState: false);

	private nint _signal;

	private Thread? _thread;

	private LoopbackStream? _stream;

	private short[]? _convert;

	private bool _unavailableLogged;

	private volatile bool _stopping;

	private volatile bool _reopenRequested;

	private volatile bool _dead;

	private volatile EndpointOpenFailure? _unavailable;

	private volatile Exception? _failure;

	private volatile PendingInterruption? _pending;

	private volatile SystemAudioMixFormat? _mixFormat;

	private volatile bool _keepalive;

	public EndpointOpenFailure? Unavailable => _unavailable;

	public Exception? Failure => _failure;

	public bool IsDead => _dead;

	public SystemAudioMixFormat? MixFormat => _mixFormat;

	public bool Keepalive => _keepalive;

	public LoopbackCapture(AudioEndpoint endpoint, SampleRing ring, AudioClock clock, CaptureCounters counters, string threadName)
	{
		_endpoint = endpoint;
		_ring = ring;
		_clock = clock;
		_counters = counters;
		_threadName = threadName;
	}

	public void Start()
	{
		_signal = CoreAudioInterop.CreateEventW(IntPtr.Zero, manualReset: false, initialState: false, IntPtr.Zero);
		_thread = new Thread(Run, 262144)
		{
			IsBackground = true,
			Name = _threadName
		};
		_thread.Start();
	}

	public bool WaitFirstProbe(int milliseconds)
	{
		return _firstProbe.Wait(milliseconds);
	}

	public void RequestReprobe()
	{
		Signal();
	}

	public void RequestReopenOnDefault()
	{
		_reopenRequested = true;
		Signal();
	}

	public PendingInterruption? ConsumePendingInterruption()
	{
		return Interlocked.Exchange(ref _pending, null);
	}

	public void Stop()
	{
		_stopping = true;
		Signal();
	}

	public bool Join(TimeSpan timeout)
	{
		Thread thread = _thread;
		if (thread == null || thread.Join(timeout))
		{
			if (_signal != IntPtr.Zero)
			{
				CoreAudioInterop.CloseHandle(_signal);
				_signal = IntPtr.Zero;
			}
			_firstProbe.Dispose();
			return true;
		}
		return false;
	}

	private void Signal()
	{
		nint signal = _signal;
		if (signal != IntPtr.Zero)
		{
			CoreAudioInterop.SetEvent(signal);
		}
	}

	private unsafe void Run()
	{
		int num = CoreAudioInterop.CoInitializeEx(IntPtr.Zero, 0u);
		nint num2 = IntPtr.Zero;
		try
		{
			uint taskIndex = 0u;
			num2 = CoreAudioInterop.AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
		}
		catch
		{
		}
		nint* ptr = stackalloc nint[2];
		ptr[1] = _signal;
		bool afterReopen = false;
		try
		{
			while (!_stopping)
			{
				if (_stream == null)
				{
					if (!TryOpen())
					{
						CoreAudioInterop.WaitForSingleObject(_signal, 5000u);
						continue;
					}
					afterReopen = true;
				}
				if (_reopenRequested)
				{
					_reopenRequested = false;
					Reopen();
					afterReopen = true;
					continue;
				}
				LoopbackStream stream = _stream;
				if (stream == null)
				{
					continue;
				}
				*ptr = stream.Event;
				uint num3 = CoreAudioInterop.WaitForMultipleObjects(2u, ptr, waitAll: false, 200u);
				if (num3 == 1)
				{
					continue;
				}
				switch (num3)
				{
				case 258u:
					_counters.CountStarved();
					break;
				case uint.MaxValue:
					throw new CoreAudioException("WaitForMultipleObjects", -2147467259);
				}
				try
				{
					DrainPackets(stream, ref afterReopen);
					TopUp(stream);
				}
				catch (CoreAudioException ex) when (CoreAudioInterop.IsInvalidated(ex.HResult))
				{
					CloseStream();
					Interlocked.CompareExchange(ref _pending, new PendingInterruption("endpoint_invalidated", ex.HResult), null);
					if (Reopen())
					{
						_counters.CountReopen();
					}
					afterReopen = true;
				}
			}
		}
		catch (Exception failure)
		{
			_failure = failure;
		}
		finally
		{
			CloseStream();
			if (num2 != IntPtr.Zero)
			{
				CoreAudioInterop.AvRevertMmThreadCharacteristics(num2);
			}
			if (num >= 0)
			{
				CoreAudioInterop.CoUninitialize();
			}
			_dead = true;
			_firstProbe.Set();
		}
	}

	private bool TryOpen()
	{
		if (_endpoint.TryOpenDefault(out LoopbackStream stream, out EndpointOpenFailure failure))
		{
			_stream = stream;
			_convert = ((stream.Layout != PacketSampleLayout.Int16 || stream.PacketChannels != 2) ? new short[stream.BufferFrames * 2] : null);
			_mixFormat = stream.MixFormat;
			_keepalive = stream.Keepalive;
			_unavailable = null;
			_unavailableLogged = false;
			_firstProbe.Set();
			return true;
		}
		_unavailable = failure;
		if ((object)failure.MixFormat != null)
		{
			_mixFormat = failure.MixFormat;
		}
		if (!_unavailableLogged)
		{
			_unavailableLogged = true;
			Log.Capture($"system audio unavailable reason={failure.Reason} hr=0x{failure.Hr:X8}", "TryOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\LoopbackCapture.cs", 274);
		}
		_firstProbe.Set();
		return false;
	}

	private bool Reopen()
	{
		CloseStream();
		while (!_stopping)
		{
			if (TryOpen())
			{
				return true;
			}
			CoreAudioInterop.WaitForSingleObject(_signal, 500u);
		}
		return false;
	}

	private void CloseStream()
	{
		LoopbackStream? stream = _stream;
		_stream = null;
		_keepalive = false;
		stream?.Dispose();
	}

	private unsafe void DrainPackets(LoopbackStream stream, ref bool afterReopen)
	{
		AudioCaptureClient captureService = stream.CaptureService;
		while (true)
		{
			Check(captureService.GetNextPacketSize(out var frames), "GetNextPacketSize");
			if (frames == 0)
			{
				break;
			}
			int buffer = captureService.GetBuffer(out var data, out var frames2, out var flags, out var _, out var qpcPosition);
			if (buffer == 143196161)
			{
				break;
			}
			Check(buffer, "GetBuffer");
			if (frames2 != 0)
			{
				Place(stream, data, frames2, flags, qpcPosition, afterReopen);
				afterReopen = false;
			}
			Check(captureService.ReleaseBuffer(frames2), "ReleaseBuffer");
		}
	}

	private unsafe void Place(LoopbackStream stream, byte* data, uint frames, uint flags, ulong qpcPosition, bool afterReopen)
	{
		int num = (int)Math.Min(frames, stream.BufferFrames);
		long stampIndex = _clock.IndexFromQpcPositionHns(qpcPosition);
		bool flag = (flags & 2) != 0;
		ReadOnlySpan<short> frames2;
		if (_convert == null)
		{
			frames2 = new ReadOnlySpan<short>(data, num * 2);
		}
		else
		{
			if (!flag)
			{
				Convert(stream, data, num, _convert);
			}
			frames2 = _convert.AsSpan(0, num * 2);
		}
		_ring.Place(stampIndex, frames2, flags, afterReopen);
	}

	private unsafe static void Convert(LoopbackStream stream, byte* data, int frames, short[] destination)
	{
		int packetChannels = stream.PacketChannels;
		int num = Math.Min(1, packetChannels - 1);
		switch (stream.Layout)
		{
		case PacketSampleLayout.Float32:
		{
			for (int j = 0; j < frames; j++)
			{
				destination[j * 2] = ClampFloat(((float*)data)[j * packetChannels]);
				destination[j * 2 + 1] = ClampFloat(((float*)data)[j * packetChannels + num]);
			}
			break;
		}
		case PacketSampleLayout.Int32:
		{
			for (int k = 0; k < frames; k++)
			{
				destination[k * 2] = (short)(((int*)data)[k * packetChannels] >> 16);
				destination[k * 2 + 1] = (short)(((int*)data)[k * packetChannels + num] >> 16);
			}
			break;
		}
		default:
		{
			for (int i = 0; i < frames; i++)
			{
				destination[i * 2] = ((short*)data)[i * packetChannels];
				destination[i * 2 + 1] = ((short*)data)[i * packetChannels + num];
			}
			break;
		}
		}
	}

	private static short ClampFloat(float sample)
	{
		float num = sample * 32767f;
		if (num >= 32767f)
		{
			return short.MaxValue;
		}
		if (num <= -32768f)
		{
			return short.MinValue;
		}
		return (short)num;
	}

	private void TopUp(LoopbackStream stream)
	{
		int num = stream.TopUpKeepalive();
		if (num < 0)
		{
			if (CoreAudioInterop.IsInvalidated(num))
			{
				throw new CoreAudioException("keepalive", num);
			}
			stream.StopKeepalive();
			_keepalive = false;
		}
	}

	private static void Check(int hr, string call)
	{
		if (hr < 0)
		{
			throw new CoreAudioException(call, hr);
		}
	}
}
