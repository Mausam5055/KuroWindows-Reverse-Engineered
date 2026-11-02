using System.Runtime.CompilerServices;

namespace KuroWindows.Input;

[InlineArray(8)]
public struct TouchContacts
{
	public const int Capacity = 8;

	private TouchContact _element0;
}
