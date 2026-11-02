using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClientTelemetry;

internal sealed class PersistentState
{
	private readonly string _path;

	private long _seq;

	private string? _deviceId;

	public Guid InstallId { get; }

	public string? DeviceId => Volatile.Read(in _deviceId);

	private PersistentState(string path, Guid installId, long seq, string? deviceId)
	{
		_path = path;
		InstallId = installId;
		_seq = seq;
		_deviceId = deviceId;
	}

	public void RememberDevice(string deviceId)
	{
		if (Guid.TryParse(deviceId, out var result))
		{
			string text = result.ToString();
			if (!(text == Volatile.Read(in _deviceId)))
			{
				Volatile.Write(ref _deviceId, text);
				Save();
			}
		}
	}

	public long NextSeq()
	{
		return Interlocked.Increment(ref _seq);
	}

	public static PersistentState Load(string stateDirectory)
	{
		Directory.CreateDirectory(stateDirectory);
		string path = Path.Combine(stateDirectory, "state.json");
		try
		{
			if (File.Exists(path))
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllBytes(path));
				JsonElement rootElement = jsonDocument.RootElement;
				if (rootElement.TryGetProperty("install_id", out var value) && Guid.TryParse(value.GetString(), out var result))
				{
					long seq = ((rootElement.TryGetProperty("seq", out var value2) && value2.TryGetInt64(out var value3)) ? value3 : 0);
					string deviceId = ((rootElement.TryGetProperty("device_id", out var value4) && Guid.TryParse(value4.GetString(), out var result2)) ? result2.ToString() : null);
					return new PersistentState(path, result, seq, deviceId);
				}
			}
		}
		catch (Exception)
		{
		}
		PersistentState persistentState = new PersistentState(path, Guid.NewGuid(), 0L, null);
		persistentState.Save();
		return persistentState;
	}

	public void Save()
	{
		try
		{
			string text = _path + ".tmp";
			string text2 = Volatile.Read(in _deviceId);
			string value = ((text2 == null) ? "" : (",\"device_id\":\"" + text2 + "\""));
			File.WriteAllText(text, $"{{\"install_id\":\"{InstallId}\",\"seq\":{Interlocked.Read(in _seq)}{value}}}");
			File.Move(text, _path, overwrite: true);
		}
		catch (Exception)
		{
		}
	}
}
