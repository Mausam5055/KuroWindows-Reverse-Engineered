using System;

namespace KuroWindows.Auth;

public class DeviceCheckException(DeviceCheckErrorKind kind, string? message = null) : Exception(message)
{
	public DeviceCheckErrorKind Kind { get; } = kind;
}
