namespace KuroWindows.Input;

internal readonly record struct ChannelCapabilities(bool Hooks, bool RawMouse, bool RawKeyboard, bool Touchpad, bool WinEvents, bool Clipboard);
