using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KuroWindows.Capture;

internal static class CursorShapes
{
	public const string Hidden = "hidden";

	public const string Custom = "custom";

	private static readonly (int Id, string Name)[] Stock = new (int, string)[16]
	{
		(32512, "arrow"),
		(32513, "ibeam"),
		(32514, "wait"),
		(32515, "cross"),
		(32516, "uparrow"),
		(32642, "sizenwse"),
		(32643, "sizenesw"),
		(32644, "sizewe"),
		(32645, "sizens"),
		(32646, "sizeall"),
		(32648, "no"),
		(32649, "hand"),
		(32650, "appstarting"),
		(32651, "help"),
		(32671, "pin"),
		(32672, "person")
	};

	private static readonly Lazy<Dictionary<nint, string>> Table = new Lazy<Dictionary<nint, string>>(Load);

	public static string Classify(nint hCursor, bool visible)
	{
		if (!visible)
		{
			return "hidden";
		}
		if (!Table.Value.TryGetValue(hCursor, out string value))
		{
			return "custom";
		}
		return value;
	}

	private static Dictionary<nint, string> Load()
	{
		Dictionary<nint, string> dictionary = new Dictionary<nint, string>(Stock.Length);
		(int, string)[] stock = Stock;
		for (int i = 0; i < stock.Length; i++)
		{
			(int, string) tuple = stock[i];
			int item = tuple.Item1;
			string item2 = tuple.Item2;
			nint num = LoadCursorW(IntPtr.Zero, new IntPtr(item));
			if (num != IntPtr.Zero)
			{
				dictionary.TryAdd(num, item2);
			}
		}
		return dictionary;
	}

	[DllImport("user32.dll")]
	private static extern nint LoadCursorW(nint hInstance, nint lpCursorName);
}
