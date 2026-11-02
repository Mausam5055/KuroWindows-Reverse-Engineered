using System;
using System.Collections.Generic;

namespace ClientTelemetry;

internal sealed class SignalDeduper
{
	private sealed class Entry
	{
		public long FirstMono;

		public int Suppressed;

		public TelemetryEvent? Latest;
	}

	public static readonly TimeSpan Window = TimeSpan.FromMinutes(5.0);

	public const int MaxTracked = 256;

	private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

	public bool Admit(TelemetryEvent e, long mono, out TelemetryEvent? closed)
	{
		closed = null;
		if (e.Kind != Kind.Signal || e.Severity < Severity.Warn)
		{
			return true;
		}
		string key = e.DedupeKey();
		if (_entries.TryGetValue(key, out Entry value))
		{
			if ((double)(mono - value.FirstMono) < Window.TotalMilliseconds)
			{
				value.Suppressed++;
				value.Latest = e;
				return false;
			}
			closed = Summary(value);
		}
		else if (_entries.Count >= 256)
		{
			return true;
		}
		_entries[key] = new Entry
		{
			FirstMono = mono
		};
		return true;
	}

	public IEnumerable<TelemetryEvent> Expire(long mono)
	{
		List<string> done = null;
		foreach (var (item, entry2) in _entries)
		{
			if (!((double)(mono - entry2.FirstMono) < Window.TotalMilliseconds))
			{
				List<string> list = done;
				if (list == null)
				{
					List<string> list2;
					done = (list2 = new List<string>());
					list = list2;
				}
				list.Add(item);
				TelemetryEvent telemetryEvent = Summary(entry2);
				if (telemetryEvent != null)
				{
					yield return telemetryEvent;
				}
			}
		}
		if (done == null)
		{
			yield break;
		}
		foreach (string item2 in done)
		{
			_entries.Remove(item2);
		}
	}

	public IEnumerable<TelemetryEvent> Drain()
	{
		return Expire(4611686018427387903L);
	}

	private static TelemetryEvent? Summary(Entry entry)
	{
		if (entry.Suppressed != 0)
		{
			TelemetryEvent latest = entry.Latest;
			if (latest != null)
			{
				latest.Repeats = entry.Suppressed;
				return latest;
			}
		}
		return null;
	}
}
