using System;
using System.Collections.Generic;

namespace ClientTelemetry;

public sealed class Attributes
{
	internal const int MaxIdentifierChars = 256;

	internal const int MaxTextChars = 8192;

	private readonly List<KeyValuePair<string, object>> _items;

	public int Count => _items.Count;

	internal IReadOnlyList<KeyValuePair<string, object>> Items => _items;

	public Attributes(int capacity = 8)
	{
		_items = new List<KeyValuePair<string, object>>(capacity);
	}

	public void Add(string name, string value)
	{
		_items.Add(new KeyValuePair<string, object>(name, Truncate(value, 256)));
	}

	public void Add(string name, long value)
	{
		_items.Add(new KeyValuePair<string, object>(name, value));
	}

	public void Add(string name, double value)
	{
		_items.Add(new KeyValuePair<string, object>(name, value));
	}

	public void Add(string name, bool value)
	{
		_items.Add(new KeyValuePair<string, object>(name, value));
	}

	public void Add(string name, Guid value)
	{
		_items.Add(new KeyValuePair<string, object>(name, value));
	}

	public void AddText(string name, string value, int max)
	{
		_items.Add(new KeyValuePair<string, object>(name, new Text(Truncate(value, Math.Min(max, 8192)))));
	}

	public void AddIdentifier(string name, string value)
	{
		_items.Add(new KeyValuePair<string, object>(name, Identifier(value)));
	}

	internal static string Identifier(string value)
	{
		if (value.Length == 0)
		{
			return "-";
		}
		Span<char> span = stackalloc char[Math.Min(value.Length, 256)];
		for (int i = 0; i < span.Length; i++)
		{
			char c = value[i];
			int index = i;
			bool flag = char.IsAsciiLetterOrDigit(c);
			if (!flag)
			{
				bool flag2;
				switch (c)
				{
				case '-':
				case '.':
				case ':':
				case '_':
					flag2 = true;
					break;
				default:
					flag2 = false;
					break;
				}
				flag = flag2;
			}
			span[index] = (flag ? c : '_');
		}
		return new string(span);
	}

	internal void AddMap(string name, IReadOnlyDictionary<string, object> value)
	{
		_items.Add(new KeyValuePair<string, object>(name, value));
	}

	private static string Truncate(string s, int max)
	{
		if (s.Length > max)
		{
			return s.Substring(0, max);
		}
		return s;
	}
}
