using System;

namespace KuroWindows.Input;

[Flags]
public enum RowFlags : uint
{
	None = 0u,
	IsDown = 1u,
	Injected = 2u,
	LowerIl = 4u,
	IsRepeat = 8u,
	Extended = 0x10u,
	Dead = 0x20u,
	IsPrecise = 0x40u,
	HasDisplayIndex = 0x80u,
	HasTarget = 0x100u,
	HasExtraInfo = 0x200u,
	Ended = 0x400u,
	Coalesced = 0x800u
}
