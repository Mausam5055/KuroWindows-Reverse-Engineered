namespace KuroWindows.Input;

public sealed record InputStateSnapshot(long Qpc, uint[] KeysDown, bool Left, bool Right, bool Middle, bool Other3, bool Other4, bool Caps, bool Num, bool Scroll, uint Modifiers)
{
	public object ToRow(string reason, long timestamp)
	{
		return new
		{
			type = "input_state",
			reason = reason,
			keysDown = KeysDown,
			buttons = new
			{
				left = Left,
				right = Right,
				middle = Middle,
				other_3 = Other3,
				other_4 = Other4
			},
			toggles = new
			{
				caps = Caps,
				num = Num,
				scroll = Scroll
			},
			modifiers = Modifiers,
			timestamp = timestamp
		};
	}
}
