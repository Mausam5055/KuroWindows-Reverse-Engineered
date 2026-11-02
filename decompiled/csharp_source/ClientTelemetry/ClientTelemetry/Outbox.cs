using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClientTelemetry;

internal sealed class Outbox
{
	public const int MaxFiles = 10000;

	public const long DefaultMaxBytes = 67108864L;

	private const long MaxReadBytes = 2097152L;

	private readonly string _pending;

	private readonly string _inflight;

	private readonly Action<string>? _log;

	private readonly long _maxBytes;

	public bool HasPending => SafeList(_pending, "*.json").Length != 0;

	public int PendingCount => SafeList(_pending, "*.json").Length;

	public Outbox(string root, Action<string>? log, long maxBytes = 67108864L)
	{
		_pending = Path.Combine(root, "pending");
		_inflight = Path.Combine(root, "inflight");
		_log = log;
		_maxBytes = maxBytes;
		Directory.CreateDirectory(_pending);
		Directory.CreateDirectory(_inflight);
	}

	public void Recover()
	{
		string[] array = SafeList(_inflight, "*.json");
		foreach (string text in array)
		{
			string destFileName = Path.Combine(_pending, Path.GetFileName(text));
			try
			{
				File.Move(text, destFileName, overwrite: true);
			}
			catch (Exception ex)
			{
				_log?.Invoke("outbox recover failed: " + ex.GetType().Name);
				TryDelete(text);
			}
		}
		array = new string[2] { _pending, _inflight };
		for (int i = 0; i < array.Length; i++)
		{
			string[] array2 = SafeList(array[i], "*.tmp");
			for (int j = 0; j < array2.Length; j++)
			{
				TryDelete(array2[j]);
			}
		}
	}

	public int Seal(Guid batchId, DateTimeOffset now, long order, string appVersion, byte[] eventsJson)
	{
		string value = (IsFileSafe(appVersion) ? (appVersion + "-") : "");
		string path = $"{now.ToUnixTimeMilliseconds():D13}-{order:D12}-{value}{batchId:N}.json";
		string text = Path.Combine(_pending, path);
		string text2 = text + ".tmp";
		File.WriteAllBytes(text2, eventsJson);
		File.Move(text2, text, overwrite: true);
		return EnforceQuota();
	}

	public OutboxItem? TakeOldest(out int corruptDeleted)
	{
		corruptDeleted = 0;
		foreach (string item in SafeList(_pending, "*.json").OrderBy((string x) => x, StringComparer.Ordinal))
		{
			string text = Path.Combine(_inflight, Path.GetFileName(item));
			try
			{
				File.Move(item, text, overwrite: true);
				OutboxItem outboxItem = Read(text);
				if ((object)outboxItem != null)
				{
					return outboxItem;
				}
				corruptDeleted++;
				TryDelete(text);
			}
			catch (Exception ex)
			{
				_log?.Invoke("outbox take failed: " + ex.GetType().Name);
				corruptDeleted++;
				TryDelete(text);
				TryDelete(item);
			}
		}
		return null;
	}

	public void Complete(OutboxItem item)
	{
		TryDelete(item.Path);
	}

	public void Release(OutboxItem item)
	{
		string destFileName = Path.Combine(_pending, Path.GetFileName(item.Path));
		try
		{
			File.Move(item.Path, destFileName, overwrite: true);
		}
		catch (Exception ex)
		{
			_log?.Invoke("outbox release failed: " + ex.GetType().Name);
			TryDelete(item.Path);
		}
	}

	private static OutboxItem? Read(string path)
	{
		FileInfo fileInfo = new FileInfo(path);
		if (!fileInfo.Exists || fileInfo.Length == 0L || fileInfo.Length > 2097152)
		{
			return null;
		}
		byte[] array = File.ReadAllBytes(path);
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(array);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
			{
				return null;
			}
			int arrayLength = jsonDocument.RootElement.GetArrayLength();
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
			Guid batchId;
			if (fileNameWithoutExtension.Length >= 32)
			{
				if (Guid.TryParseExact(fileNameWithoutExtension.Substring(fileNameWithoutExtension.Length - 32), "N", out var result))
				{
					batchId = result;
					goto IL_00a8;
				}
			}
			batchId = Guid.NewGuid();
			goto IL_00a8;
			IL_00a8:
			object obj;
			if (fileNameWithoutExtension.Length <= 60)
			{
				obj = null;
			}
			else
			{
				string text = fileNameWithoutExtension;
				obj = text.Substring(27, text.Length - 33 - 27);
			}
			string text2 = (string)obj;
			return new OutboxItem(batchId, path, array, arrayLength, (text2 != null && IsFileSafe(text2)) ? text2 : null);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static bool IsFileSafe(string v)
	{
		int length = v.Length;
		if (length > 0 && length <= 64)
		{
			return v.All((char c) =>
			{
				bool flag = char.IsAsciiLetterOrDigit(c);
				if (!flag)
				{
					bool flag2;
					switch (c)
					{
					case '+':
					case '-':
					case '.':
						flag2 = true;
						break;
					default:
						flag2 = false;
						break;
					}
					flag = flag2;
				}
				return flag;
			});
		}
		return false;
	}

	private int EnforceQuota()
	{
		List<FileInfo> list = (from f in SafeList(_pending, "*.json").OrderBy((string x) => x, StringComparer.Ordinal)
			select new FileInfo(f)).ToList();
		long num = list.Sum((FileInfo f) => f.Length);
		int num2 = 0;
		for (int num3 = 0; num3 < list.Count && (num > _maxBytes || list.Count - num3 > 10000); num3++)
		{
			num -= list[num3].Length;
			TryDelete(list[num3].FullName);
			num2++;
		}
		return num2;
	}

	private static string[] SafeList(string dir, string pattern)
	{
		try
		{
			return Directory.GetFiles(dir, pattern);
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
		}
	}
}
