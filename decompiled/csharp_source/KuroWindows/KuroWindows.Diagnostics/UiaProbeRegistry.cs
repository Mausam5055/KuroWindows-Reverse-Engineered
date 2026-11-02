using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Diagnostics;

internal static class UiaProbeRegistry
{
	internal sealed record Entry
	{
		[JsonPropertyName("pid")]
		public int Pid { get; init; }

		[JsonPropertyName("spawned_at")]
		public DateTimeOffset SpawnedAt { get; init; }

		[JsonPropertyName("exited_at")]
		public DateTimeOffset? ExitedAt { get; init; }
	}

	private static readonly TimeSpan MaxEntryAge = TimeSpan.FromDays(7.0);

	private static readonly TimeSpan OpenWindowCap = TimeSpan.FromHours(12.0);

	private static readonly TimeSpan WindowSlack = TimeSpan.FromSeconds(60.0);

	private const int MaxEntries = 64;

	private static readonly object Sync = new object();

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true
	};

	private static string _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "diagnostics", "uia-probe-runs.json");

	internal static void OverridePathForTests(string path)
	{
		lock (Sync)
		{
			_filePath = path;
		}
	}

	internal static void RecordSpawn(int pid)
	{
		if (pid <= 0)
		{
			return;
		}
		Mutate((List<Entry> entries) =>
		{
			entries.RemoveAll((Entry entry) => entry.Pid == pid && !entry.ExitedAt.HasValue);
			entries.Add(new Entry
			{
				Pid = pid,
				SpawnedAt = DateTimeOffset.UtcNow
			});
		});
	}

	internal static void RecordExit(int pid)
	{
		if (pid <= 0)
		{
			return;
		}
		Mutate((List<Entry> entries) =>
		{
			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i].Pid == pid && !entries[i].ExitedAt.HasValue)
				{
					entries[i] = entries[i] with
					{
						ExitedAt = DateTimeOffset.UtcNow
					};
				}
			}
		});
	}

	internal static IReadOnlyList<Entry> Read()
	{
		lock (Sync)
		{
			return ReadLocked();
		}
	}

	internal static bool IsHelperRecord(IReadOnlyList<Entry> entries, int? pid, DateTimeOffset? recordTime, int? priorRunPid)
	{
		if (pid.HasValue && pid.GetValueOrDefault() > 0 && recordTime.HasValue)
		{
			DateTimeOffset valueOrDefault = recordTime.GetValueOrDefault();
			if (priorRunPid.HasValue && priorRunPid.GetValueOrDefault() > 0 && pid == priorRunPid)
			{
				return false;
			}
			foreach (Entry entry in entries)
			{
				if (entry.Pid == pid)
				{
					DateTimeOffset dateTimeOffset = entry.SpawnedAt - WindowSlack;
					DateTimeOffset dateTimeOffset2 = (entry.ExitedAt ?? (entry.SpawnedAt + OpenWindowCap)) + WindowSlack;
					if (valueOrDefault >= dateTimeOffset && valueOrDefault <= dateTimeOffset2)
					{
						return true;
					}
				}
			}
			return false;
		}
		return false;
	}

	internal static bool IsHelperEventRecord(IReadOnlyList<Entry> entries, string? eventPid, DateTimeOffset? recordTime, int? priorRunPid)
	{
		IReadOnlyList<int> source = EventPidReadings(eventPid);
		if (!priorRunPid.HasValue || priorRunPid.GetValueOrDefault() <= 0 || !source.Contains(priorRunPid.Value))
		{
			return source.Any((int pid) => IsHelperRecord(entries, pid, recordTime, priorRunPid));
		}
		return false;
	}

	internal static IReadOnlyList<int> EventPidReadings(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<int>();
		}
		string text = value.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			if (!TryHex(text.Substring(2), out var pid))
			{
				return Array.Empty<int>();
			}
			return new int[1] { pid };
		}
		List<int> list = new List<int>(2);
		if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0)
		{
			list.Add(result);
		}
		if (TryHex(text, out var pid2) && !list.Contains(pid2))
		{
			list.Add(pid2);
		}
		return list;
		static bool TryHex(string digits, out int reference)
		{
			if (int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out reference))
			{
				return reference > 0;
			}
			return false;
		}
	}

	internal static int? ParseDumpPid(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}
		string[] array = Path.GetFileName(fileName).Split('.');
		for (int num = array.Length - 1; num >= 0; num--)
		{
			if (int.TryParse(array[num], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0)
			{
				return result;
			}
		}
		return null;
	}

	private static void Mutate(Action<List<Entry>> mutate)
	{
		try
		{
			lock (Sync)
			{
				List<Entry> list = ReadLocked().ToList();
				mutate(list);
				DateTimeOffset cutoff = DateTimeOffset.UtcNow - MaxEntryAge;
				WriteLocked(list.Where((Entry entry) => entry.SpawnedAt >= cutoff).TakeLast(64).ToArray());
			}
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("crash", "uia probe registry write failed: " + ex.Message, "Mutate", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\UiaProbeRegistry.cs", 264);
		}
	}

	private static List<Entry> ReadLocked()
	{
		try
		{
			if (!File.Exists(_filePath))
			{
				return new List<Entry>();
			}
			using FileStream utf8Json = File.OpenRead(_filePath);
			return JsonSerializer.Deserialize<List<Entry>>(utf8Json, JsonOptions) ?? new List<Entry>();
		}
		catch
		{
			return new List<Entry>();
		}
	}

	private static void WriteLocked(Entry[] entries)
	{
		string directoryName = Path.GetDirectoryName(_filePath);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		string text = $"{_filePath}.{Environment.ProcessId}.tmp";
		File.WriteAllText(text, JsonSerializer.Serialize(entries, JsonOptions));
		File.Move(text, _filePath, overwrite: true);
	}
}
