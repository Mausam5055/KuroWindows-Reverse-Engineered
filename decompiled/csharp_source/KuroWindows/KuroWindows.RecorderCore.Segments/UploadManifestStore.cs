using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class UploadManifestStore
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		Converters = { (JsonConverter)new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
	};

	private static readonly ConcurrentDictionary<string, byte> LoggedParseFailures = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly string _sessionDir;

	private readonly int _segmentIndex;

	private readonly string _path;

	private readonly object _sync = new object();

	public string Path => _path;

	public UploadManifestStore(string sessionDir, int segmentIndex)
	{
		_sessionDir = sessionDir;
		_segmentIndex = segmentIndex;
		_path = ManifestPath(sessionDir, segmentIndex);
	}

	public static string ManifestFileName(int segmentIndex)
	{
		return $"segment_{segmentIndex}.upload.json";
	}

	public static string ManifestPath(string sessionDir, int segmentIndex)
	{
		return System.IO.Path.Combine(sessionDir, ManifestFileName(segmentIndex));
	}

	public UploadManifest LoadOrCreate()
	{
		UploadManifest uploadManifest = TryLoad(_path);
		if (uploadManifest != null)
		{
			return uploadManifest;
		}
		return new UploadManifest
		{
			State = UploadManifestState.Ready,
			SessionId = System.IO.Path.GetFileName(_sessionDir),
			SegmentIndex = _segmentIndex,
			UpdatedWallMs = NowMs()
		};
	}

	public void Save(UploadManifest manifest)
	{
		lock (_sync)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
			manifest.UpdatedWallMs = NowMs();
			string text = $"{_path}.{Environment.ProcessId}.tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(manifest, JsonOptions));
			File.Move(text, _path, overwrite: true);
		}
	}

	public static UploadManifest? TryLoad(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			UploadManifest? result = JsonSerializer.Deserialize<UploadManifest>(File.ReadAllText(path), JsonOptions);
			LoggedParseFailures.TryRemove(path, out var _);
			return result;
		}
		catch (Exception ex)
		{
			if (LoggedParseFailures.TryAdd(path, 0))
			{
				KuroLog.Upload("upload manifest parse failed file=" + System.IO.Path.GetFileName(path) + "; durable upload state resets: " + ex.GetType().Name, "TryLoad", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\UploadManifest.cs", 130);
			}
			return null;
		}
	}

	public static bool IsRetryDue(string sessionDir, int segmentIndex)
	{
		long? num = TryLoad(ManifestPath(sessionDir, segmentIndex))?.NextRetryWallMs;
		if (num.HasValue)
		{
			long valueOrDefault = num.GetValueOrDefault();
			return valueOrDefault <= NowMs();
		}
		return true;
	}

	public static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}
}
