using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using SharpGen.Runtime.Win32;
using Vortice.MediaFoundation;

namespace SystemAudio;

internal sealed class AudioEndpoint : IDisposable
{
	private sealed class EndpointNotificationClient(AudioEndpoint owner) : CallbackBase, IMMNotificationClient, IUnknown, ICallbackable, IDisposable
	{
		public void OnDeviceStateChanged(string deviceId, int newState)
		{
		}

		public void OnDeviceAdded(string deviceId)
		{
		}

		public void OnDeviceRemoved(string deviceId)
		{
		}

		public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
		{
			if (flow == DataFlow.Render && role == Role.Console && !string.Equals(defaultDeviceId, owner._openEndpointId, StringComparison.Ordinal))
			{
				Interlocked.Exchange(ref owner._defaultChanged, 1);
			}
		}

		public void OnPropertyValueChanged(string deviceId, PropertyKey key)
		{
		}
	}

	private const long CaptureBufferHns = 2000000L;

	private IMMDeviceEnumerator? _enumerator;

	private EndpointNotificationClient? _client;

	private volatile string? _openEndpointId;

	private int _defaultChanged;

	private bool _disposed;

	public bool ConsumeDefaultChanged()
	{
		return Interlocked.Exchange(ref _defaultChanged, 0) != 0;
	}

	public bool TryOpenDefault(out LoopbackStream? stream, out EndpointOpenFailure failure)
	{
		stream = null;
		if (!EnsureEnumerator(out failure))
		{
			return false;
		}
		IMMDevice endpoint = null;
		try
		{
			try
			{
				_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out endpoint);
			}
			catch (Exception ex)
			{
				ref EndpointOpenFailure reference = ref failure;
				string reason;
				if (ex.HResult == -2147023728)
				{
					reason = "no_endpoint";
				}
				else
				{
					reason = ((ex.HResult == -2004287472) ? "service_not_running" : "enumerator_failed");
				}
				reference = new EndpointOpenFailure(reason, ex.HResult);
				return false;
			}
			if ((object)endpoint == null)
			{
				failure = new EndpointOpenFailure("no_endpoint", -2147023728);
				return false;
			}
			string text = ReadEndpointId(endpoint);
			stream = LoopbackStream.Open(endpoint, text, 2000000L, out failure);
			if (stream == null)
			{
				return false;
			}
			_openEndpointId = text;
			return true;
		}
		finally
		{
			endpoint?.Dispose();
		}
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
			if ((object)_enumerator != null && _client != null)
			{
				_enumerator.UnregisterEndpointNotificationCallback(_client);
			}
		}
		catch
		{
		}
		finally
		{
			_client?.Dispose();
			_client = null;
			_enumerator?.Dispose();
			_enumerator = null;
		}
	}

	private bool EnsureEnumerator(out EndpointOpenFailure failure)
	{
		failure = null;
		if ((object)_enumerator != null)
		{
			return true;
		}
		try
		{
			_enumerator = new IMMDeviceEnumerator();
		}
		catch (Exception ex)
		{
			failure = new EndpointOpenFailure((ex.HResult == -2004287472) ? "service_not_running" : "enumerator_failed", ex.HResult);
			return false;
		}
		try
		{
			_client = new EndpointNotificationClient(this);
			_enumerator.RegisterEndpointNotificationCallback(_client);
		}
		catch
		{
			_client?.Dispose();
			_client = null;
		}
		return true;
	}

	private unsafe static string ReadEndpointId(IMMDevice device)
	{
		nint num = default;
		if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(IntPtr*)((nint)(*(IntPtr*)device.NativePointer) + (nint)5 * (nint)sizeof(void*))))(device.NativePointer, &num) < 0 || num == IntPtr.Zero)
		{
			return "";
		}
		try
		{
			return Marshal.PtrToStringUni(num) ?? "";
		}
		finally
		{
			Marshal.FreeCoTaskMem(num);
		}
	}
}
