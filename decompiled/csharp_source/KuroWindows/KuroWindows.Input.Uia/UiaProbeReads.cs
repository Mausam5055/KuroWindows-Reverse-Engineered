using System;

namespace KuroWindows.Input.Uia;

[Flags]
internal enum UiaProbeReads : byte
{
	None = 0,
	SecureField = 1,
	TextSelection = 2
}
