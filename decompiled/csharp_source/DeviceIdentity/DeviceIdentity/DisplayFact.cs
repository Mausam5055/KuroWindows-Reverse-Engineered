using System.Collections.Generic;

namespace DeviceIdentity;

internal sealed record DisplayFact(string? Tech, string? Mfr, string? Pid, bool Serial, string? Adapter, bool Builtin)
{
	public Dictionary<string, object?> ToWire()
	{
		return new Dictionary<string, object>
		{
			["tech"] = Tech,
			["mfr"] = Mfr,
			["pid"] = Pid,
			["serial"] = Serial,
			["adapter"] = Adapter,
			["builtin"] = Builtin
		};
	}
}
