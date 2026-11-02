using System;
using System.Collections.Generic;

namespace ClientTelemetry;

internal sealed class CounterStore
{
	private readonly object _sync = new object();

	private readonly Dictionary<string, long> _plain = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly Dictionary<string, Dictionary<string, long>> _labeled = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

	private readonly Dictionary<string, Dictionary<string, long>> _distributions = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

	public bool IsEmpty
	{
		get
		{
			lock (_sync)
			{
				return _plain.Count == 0 && _labeled.Count == 0 && _distributions.Count == 0;
			}
		}
	}

	public void Count(string name, string? label, long by)
	{
		if (by == 0L)
		{
			return;
		}
		lock (_sync)
		{
			if (label == null)
			{
				_plain[name] = _plain.GetValueOrDefault(name) + by;
				return;
			}
			if (!_labeled.TryGetValue(name, out Dictionary<string, long> value))
			{
				value = (_labeled[name] = new Dictionary<string, long>(StringComparer.Ordinal));
			}
			value[label] = value.GetValueOrDefault(label) + by;
		}
	}

	public void Observe(string name, double value)
	{
		string key = Bucket(value);
		lock (_sync)
		{
			if (!_distributions.TryGetValue(name, out Dictionary<string, long> value2))
			{
				value2 = (_distributions[name] = new Dictionary<string, long>(StringComparer.Ordinal));
			}
			value2[key] = value2.GetValueOrDefault(key) + 1;
		}
	}

	public (IReadOnlyDictionary<string, object> Counters, IReadOnlyDictionary<string, object> Distributions) Snapshot()
	{
		lock (_sync)
		{
			Dictionary<string, object> dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
			string key;
			foreach (KeyValuePair<string, long> item in _plain)
			{
				item.Deconstruct(out key, out var value);
				string key2 = key;
				long num = value;
				dictionary[key2] = num;
			}
			Dictionary<string, long> value2;
			foreach (KeyValuePair<string, Dictionary<string, long>> item2 in _labeled)
			{
				item2.Deconstruct(out key, out value2);
				string key3 = key;
				Dictionary<string, long> dictionary2 = value2;
				dictionary[key3] = new Dictionary<string, long>(dictionary2);
			}
			Dictionary<string, object> dictionary3 = new Dictionary<string, object>(StringComparer.Ordinal);
			foreach (KeyValuePair<string, Dictionary<string, long>> distribution in _distributions)
			{
				distribution.Deconstruct(out key, out value2);
				string key4 = key;
				Dictionary<string, long> dictionary4 = value2;
				dictionary3[key4] = new Dictionary<string, long>(dictionary4);
			}
			_plain.Clear();
			_labeled.Clear();
			_distributions.Clear();
			return (Counters: dictionary, Distributions: dictionary3);
		}
	}

	internal static string Bucket(double value)
	{
		if (double.IsNaN(value) || value <= 1.0)
		{
			return "le_1";
		}
		int num = Math.Min(40, (int)Math.Ceiling(Math.Log2(value)));
		return $"le_{1L << num}";
	}
}
