using System;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace SystemAudio;

internal sealed class LoopbackStream : IDisposable
{
	private readonly AudioClient _capture;

	private readonly AudioCaptureClient _captureService;

	private AudioClient _render;

	private AudioRenderClient _renderService;

	private bool _disposed;

	public nint Event { get; }

	public string EndpointId { get; }

	public uint BufferFrames { get; }

	public uint RenderBufferFrames { get; private set; }

	public SystemAudioMixFormat MixFormat { get; }

	public PacketSampleLayout Layout { get; }

	public int PacketChannels { get; }

	public bool Keepalive { get; private set; }

	public AudioCaptureClient CaptureService => _captureService;

	private LoopbackStream(AudioClient capture, AudioCaptureClient captureService, nint eventHandle, string endpointId, uint bufferFrames, SystemAudioMixFormat mixFormat, PacketSampleLayout layout, int packetChannels)
	{
		_capture = capture;
		_captureService = captureService;
		Event = eventHandle;
		EndpointId = endpointId;
		BufferFrames = bufferFrames;
		MixFormat = mixFormat;
		Layout = layout;
		PacketChannels = packetChannels;
	}

	public unsafe static LoopbackStream? Open(IMMDevice device, string endpointId, long bufferHns, out EndpointOpenFailure failure)
	{
		failure = null;
		AudioClient client = default;
		AudioCaptureClient captureService = default;
		nint format = IntPtr.Zero;
		nint num = IntPtr.Zero;
		SystemAudioMixFormat mix = null;
		try
		{
			if (!Activate(device, out client, out var hr))
			{
				failure = Classify(hr, mix);
				return null;
			}
			hr = client.GetMixFormat(out format);
			if (hr < 0)
			{
				failure = Classify(hr, mix);
				return null;
			}
			mix = ReadMixFormat(format, out var layout, out var supported);
			CoreAudioInterop.WaveFormatEx pcm48kStereo = CoreAudioInterop.WaveFormatEx.Pcm48kStereo16;
			hr = client.Initialize(0, 2282094592u, bufferHns, 0L, &pcm48kStereo, null);
			PacketSampleLayout layout2 = PacketSampleLayout.Int16;
			int packetChannels = 2;
			if (hr < 0)
			{
				client.Release();
				client = default;
				if (mix.Rate != 48000)
				{
					failure = new EndpointOpenFailure($"capture_format:{mix.Rate}", hr, mix);
					return null;
				}
				if (!supported)
				{
					failure = new EndpointOpenFailure($"capture_format:bits{mix.Bits}", hr, mix);
					return null;
				}
				if (!Activate(device, out client, out hr))
				{
					failure = Classify(hr, mix);
					return null;
				}
				hr = client.Initialize(0, 393216u, bufferHns, 0L, (void*)format, null);
				if (hr < 0)
				{
					failure = Classify(hr, mix);
					return null;
				}
				layout2 = layout;
				packetChannels = mix.Channels;
			}
			hr = client.GetBufferSize(out var frames);
			if (hr < 0)
			{
				failure = Classify(hr, mix);
				return null;
			}
			num = CoreAudioInterop.CreateEventW(IntPtr.Zero, manualReset: false, initialState: false, IntPtr.Zero);
			if (num == IntPtr.Zero)
			{
				failure = new EndpointOpenFailure("endpoint_open_failed", -2147467259, mix);
				return null;
			}
			hr = client.SetEventHandle(num);
			if (hr >= 0)
			{
				hr = client.GetService(CoreAudioInterop.IidAudioCaptureClient, out var service);
				captureService = new AudioCaptureClient(service);
			}
			if (hr >= 0)
			{
				hr = client.Start();
			}
			if (hr < 0)
			{
				failure = Classify(hr, mix);
				return null;
			}
			LoopbackStream loopbackStream = new LoopbackStream(client, captureService, num, endpointId, frames, mix, layout2, packetChannels);
			client = default;
			captureService = default;
			num = IntPtr.Zero;
			loopbackStream.StartKeepalive(device, format, bufferHns);
			return loopbackStream;
		}
		finally
		{
			if (format != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(format);
			}
			captureService.Release();
			if (!client.IsNull)
			{
				client.Stop();
				client.Release();
			}
			if (num != IntPtr.Zero)
			{
				CoreAudioInterop.CloseHandle(num);
			}
		}
	}

	public unsafe int TopUpKeepalive()
	{
		if (!Keepalive)
		{
			return 0;
		}
		int currentPadding = _render.GetCurrentPadding(out var frames);
		if (currentPadding < 0)
		{
			return currentPadding;
		}
		uint num = ((frames < RenderBufferFrames) ? (RenderBufferFrames - frames) : 0u);
		if (num < RenderBufferFrames / 2)
		{
			return 0;
		}
		currentPadding = _renderService.GetBuffer(num, out var _);
		if (currentPadding < 0)
		{
			return currentPadding;
		}
		return _renderService.ReleaseBuffer(num, 2u);
	}

	public void StopKeepalive()
	{
		Keepalive = false;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			if (!_render.IsNull)
			{
				_render.Stop();
			}
		}
		finally
		{
			try
			{
				if (!_capture.IsNull)
				{
					_capture.Stop();
				}
			}
			finally
			{
				_renderService.Release();
				_renderService = default;
				_render.Release();
				_render = default;
				_captureService.Release();
				_capture.Release();
				if (Event != IntPtr.Zero)
				{
					CoreAudioInterop.CloseHandle(Event);
				}
			}
		}
	}

	private unsafe void StartKeepalive(IMMDevice device, nint mixPtr, long bufferHns)
	{
		AudioClient client = default;
		AudioRenderClient renderService = default;
		try
		{
			if (Activate(device, out client, out var _) && client.Initialize(0, 0u, bufferHns, 0L, (void*)mixPtr, null) >= 0 && client.GetBufferSize(out var frames) >= 0 && client.GetService(CoreAudioInterop.IidAudioRenderClient, out var service) >= 0)
			{
				renderService = new AudioRenderClient(service);
				if (renderService.GetBuffer(frames, out var _) >= 0 && renderService.ReleaseBuffer(frames, 2u) >= 0 && client.Start() >= 0)
				{
					_render = client;
					_renderService = renderService;
					RenderBufferFrames = frames;
					Keepalive = true;
					client = default;
					renderService = default;
				}
			}
		}
		finally
		{
			renderService.Release();
			client.Release();
		}
	}

	private static bool Activate(IMMDevice device, out AudioClient client, out int hr)
	{
		client = default;
		hr = 0;
		try
		{
			Guid iidAudioClient = CoreAudioInterop.IidAudioClient;
			device.Activate(iidAudioClient, 23, null, out var @interface);
			client = new AudioClient(@interface);
			return !client.IsNull;
		}
		catch (Exception ex)
		{
			hr = ex.HResult;
			return false;
		}
	}

	private static EndpointOpenFailure Classify(int hr, SystemAudioMixFormat? mix)
	{
		return new EndpointOpenFailure((hr == -2004287472) ? "service_not_running" : "endpoint_open_failed", hr, mix);
	}

	private unsafe static SystemAudioMixFormat ReadMixFormat(nint pointer, out PacketSampleLayout layout, out bool supported)
	{
		CoreAudioInterop.WaveFormatEx waveFormatEx = *(CoreAudioInterop.WaveFormatEx*)pointer;
		bool flag = waveFormatEx.FormatTag == 3;
		bool flag2 = waveFormatEx.FormatTag == 1;
		if (waveFormatEx.FormatTag == 65534 && waveFormatEx.Size >= 22)
		{
			CoreAudioInterop.WaveFormatExtensibleTail waveFormatExtensibleTail = *(CoreAudioInterop.WaveFormatExtensibleTail*)(pointer + 18);
			flag = waveFormatExtensibleTail.SubFormat == CoreAudioInterop.SubtypeIeeeFloat;
			flag2 = waveFormatExtensibleTail.SubFormat == CoreAudioInterop.SubtypePcm;
		}
		supported = true;
		if (flag && waveFormatEx.BitsPerSample == 32)
		{
			layout = PacketSampleLayout.Float32;
		}
		else if (flag2 && waveFormatEx.BitsPerSample == 32)
		{
			layout = PacketSampleLayout.Int32;
		}
		else if (flag2 && waveFormatEx.BitsPerSample == 16)
		{
			layout = PacketSampleLayout.Int16;
		}
		else
		{
			layout = PacketSampleLayout.Int16;
			supported = false;
		}
		return new SystemAudioMixFormat((int)waveFormatEx.SamplesPerSec, waveFormatEx.Channels, waveFormatEx.BitsPerSample);
	}
}
