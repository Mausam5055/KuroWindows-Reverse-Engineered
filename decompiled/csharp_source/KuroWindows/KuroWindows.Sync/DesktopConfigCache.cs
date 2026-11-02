using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed class DesktopConfigCache
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly string _path;

	private bool _loggedSaveFailure;

	public DesktopConfigCache(string? path = null)
	{
		_path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "desktop_config.json");
	}

	public CachedDesktopConfig? Load()
	{
		try
		{
			if (!File.Exists(_path))
			{
				return null;
			}
			CachedDesktopConfig cachedDesktopConfig = JsonSerializer.Deserialize<CachedDesktopConfig>(File.ReadAllText(_path), JsonOptions);
			if (cachedDesktopConfig == null)
			{
				KuroLog.Sync("DesktopConfigCache: ignoring empty cached config", "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopConfigCache.cs", 40);
				return null;
			}
			if (!cachedDesktopConfig.TryValidate(out string error))
			{
				KuroLog.Sync("DesktopConfigCache: ignoring invalid cached config: " + error, "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopConfigCache.cs", 46);
				return null;
			}
			return cachedDesktopConfig with
			{
				Config = cachedDesktopConfig.Config.Normalize()
			};
		}
		catch (Exception ex)
		{
			KuroLog.Sync("DesktopConfigCache: load failed: " + ex.Message, "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopConfigCache.cs", 57);
			return null;
		}
	}

	public void Save(long configVersion, DesktopConfig config)
	{
		DesktopConfig desktopConfig = config.Normalize();
		if (!desktopConfig.TryValidate(out string error))
		{
			throw new InvalidOperationException("Cannot cache invalid desktop config: " + error);
		}
		CachedDesktopConfig cachedDesktopConfig = new CachedDesktopConfig
		{
			SchemaVersion = 7,
			ConfigVersion = configVersion,
			CachedAt = DateTimeOffset.UtcNow,
			Config = desktopConfig
		};
		if (!cachedDesktopConfig.TryValidate(out error))
		{
			throw new InvalidOperationException("Cannot cache desktop config for this client: " + error);
		}
		string directoryName = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		string text = _path + ".tmp";
		string contents = JsonSerializer.Serialize(cachedDesktopConfig, JsonOptions);
		try
		{
			File.WriteAllText(text, contents);
			File.Move(text, _path, overwrite: true);
			_loggedSaveFailure = false;
		}
		catch (Exception ex)
		{
			if (!_loggedSaveFailure)
			{
				_loggedSaveFailure = true;
				KuroLog.Sync("DesktopConfigCache: save failed: " + ex.Message, "Save", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopConfigCache.cs", 104);
			}
			throw;
		}
	}

	public void Delete()
	{
		try
		{
			if (File.Exists(_path))
			{
				File.Delete(_path);
				KuroLog.Sync("DesktopConfigCache: cached config deleted", "Delete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopConfigCache.cs", 117);
			}
		}
		catch (Exception ex)
		{
			KuroLog.Sync("DesktopConfigCache: delete failed: " + ex.Message, "Delete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopConfigCache.cs", 122);
		}
	}
}
