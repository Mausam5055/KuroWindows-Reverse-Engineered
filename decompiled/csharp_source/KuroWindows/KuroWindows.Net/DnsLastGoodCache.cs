using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Net;

internal sealed class DnsLastGoodCache
{
	private sealed record HostEntry(List<IPAddress> Ips, DateTimeOffset UpdatedAt);

	private sealed record CacheFile
	{
		[JsonPropertyName("schema_version")]
		public int SchemaVersion { get; init; } = 1;

		[JsonPropertyName("hosts")]
		public Dictionary<string, CacheHostModel>? Hosts { get; init; }
	}

	private sealed record CacheHostModel
	{
		[JsonPropertyName("ips")]
		public List<string>? Ips { get; init; }

		[JsonPropertyName("updated_at")]
		public DateTimeOffset UpdatedAt { get; init; }
	}

	internal const int MaxHosts = 16;

	internal const int MaxIpsPerHost = 8;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	private readonly string _path;

	private readonly object _lock = new object();

	private readonly Dictionary<string, HostEntry> _hosts = new Dictionary<string, HostEntry>(StringComparer.OrdinalIgnoreCase);

	private bool _loaded;

	private bool _loggedSaveFailure;

	public string StorePath => _path;

	public DnsLastGoodCache(string? path = null)
	{
		_path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "dns-last-good.json");
	}

	public void Load()
	{
		lock (_lock)
		{
			if (_loaded)
			{
				return;
			}
			_loaded = true;
			try
			{
				if (!File.Exists(_path))
				{
					return;
				}
				CacheFile cacheFile = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path), JsonOptions);
				if (cacheFile?.Hosts == null)
				{
					return;
				}
				foreach (var (text2, cacheHostModel2) in cacheFile.Hosts)
				{
					if (string.IsNullOrWhiteSpace(text2) || cacheHostModel2?.Ips == null)
					{
						continue;
					}
					List<IPAddress> list = new List<IPAddress>();
					foreach (string ip in cacheHostModel2.Ips)
					{
						if (IPAddress.TryParse(ip, out IPAddress address) && !list.Contains(address))
						{
							list.Add(address);
						}
						if (list.Count >= 8)
						{
							break;
						}
					}
					if (list.Count > 0 && _hosts.Count < 16)
					{
						_hosts[text2] = new HostEntry(list, cacheHostModel2.UpdatedAt);
					}
				}
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("app", $"dns cache read failed ({ex.GetType().Name}: {ex.Message}); starting empty", "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\DnsLastGoodCache.cs", 101);
				_hosts.Clear();
			}
		}
	}

	public IReadOnlyList<IPAddress> Get(string host)
	{
		lock (_lock)
		{
			Load();
			HostEntry value;
			return _hosts.TryGetValue(host, out value) ? value.Ips.ToArray() : Array.Empty<IPAddress>();
		}
	}

	public void Refresh(string host, IReadOnlyList<IPAddress> addresses, IPAddress? promote = null)
	{
		if (string.IsNullOrWhiteSpace(host) || addresses.Count == 0)
		{
			return;
		}
		List<IPAddress> list = new List<IPAddress>(Math.Min(addresses.Count + 1, 8));
		if (promote != null)
		{
			list.Add(promote);
		}
		foreach (IPAddress address in addresses)
		{
			if (list.Count >= 8)
			{
				break;
			}
			if (!list.Contains(address))
			{
				list.Add(address);
			}
		}
		lock (_lock)
		{
			Load();
			if (!_hosts.TryGetValue(host, out HostEntry value) || value.Ips.Count != list.Count || value.Ips.Except(list).Any())
			{
				_hosts[host] = new HostEntry(list, DateTimeOffset.UtcNow);
				EvictOverCapLocked();
				SaveLocked();
			}
		}
	}

	public void Promote(string host, IPAddress connected)
	{
		lock (_lock)
		{
			Load();
			if (_hosts.TryGetValue(host, out HostEntry value) && value.Ips.Count != 0 && !value.Ips[0].Equals(connected))
			{
				List<IPAddress> list = new List<IPAddress>(value.Ips.Count) { connected };
				list.AddRange(value.Ips.Where((IPAddress ip) => !ip.Equals(connected)));
				_hosts[host] = new HostEntry(list, DateTimeOffset.UtcNow);
				SaveLocked();
			}
		}
	}

	private void EvictOverCapLocked()
	{
		while (_hosts.Count > 16)
		{
			string key = _hosts.OrderBy((KeyValuePair<string, HostEntry> kv) => kv.Value.UpdatedAt).First().Key;
			_hosts.Remove(key);
		}
	}

	private void SaveLocked()
	{
		try
		{
			CacheFile value = new CacheFile
			{
				Hosts = _hosts.ToDictionary((KeyValuePair<string, HostEntry> kv) => kv.Key, (KeyValuePair<string, HostEntry> kv) => new CacheHostModel
				{
					Ips = kv.Value.Ips.Select((IPAddress ip) => ip.ToString()).ToList(),
					UpdatedAt = kv.Value.UpdatedAt
				})
			};
			Directory.CreateDirectory(Path.GetDirectoryName(_path));
			string text = _path + "." + Environment.ProcessId + ".tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(value, JsonOptions));
			File.Move(text, _path, overwrite: true);
		}
		catch (Exception ex)
		{
			if (!_loggedSaveFailure)
			{
				_loggedSaveFailure = true;
				KuroLog.LocalOnly("app", $"dns cache write failed ({ex.GetType().Name}: {ex.Message})", "SaveLocked", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\DnsLastGoodCache.cs", 237);
			}
		}
	}
}
