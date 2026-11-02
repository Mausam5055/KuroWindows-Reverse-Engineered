using System;

namespace KuroWindows.CoreBridge;

public sealed class CoreBridgeCommandException : Exception
{
	public string Code { get; }

	public CoreBridgeCommandException(string code, string message)
		: base(message)
	{
		Code = code;
	}
}
