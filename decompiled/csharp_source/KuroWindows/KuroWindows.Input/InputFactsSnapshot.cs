using System.Collections.Generic;

namespace KuroWindows.Input;

internal sealed record InputFactsSnapshot(long Events, long Injected, long InjectedMouse, long InjectedKey, IReadOnlyList<InputExtraInfoFact> ExtraInfo, long RawPackets, long RawNullDevice, long RawAbsolute, IReadOnlyList<InputDeviceFact> Devices, long RawKeyboard, long RawFailures, long Rearms, long GapMs, long GamepadPackets, TouchpadFactsSnapshot? Touchpad);
