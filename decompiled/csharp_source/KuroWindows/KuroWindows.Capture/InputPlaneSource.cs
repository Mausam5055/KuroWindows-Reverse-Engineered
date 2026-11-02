using System;
using System.Collections.Generic;
using KuroWindows.Input;
using KuroWindows.Input.Touchpad;

namespace KuroWindows.Capture;

internal sealed record InputPlaneSource(Func<KuroWindows.Input.AppContext?> Foreground, Func<ActiveAppSnapshot?> ForegroundWindow, Func<ChannelCapabilities> Channels, Func<IReadOnlyList<TouchpadDeviceRecord>?> Touchpad, Func<InputSettingsSnapshot?> Settings, Func<InputStateSnapshot?> HeldState, Action<InputStateSnapshot> SeedHeldState, Func<long> LastDeliveryQpc, Func<long> LastRearmQpc, Func<BlindFlags> BlindFlags, Func<bool> SecureDesktopOwnsInput, Action PostRearm, Action PostReconcileButtons, Func<long> ButtonHeldSinceQpc);
