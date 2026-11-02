using System;

namespace KuroWindows.Input.Uia;

internal interface IUiaProbeTransport : IDisposable
{
	int Pid { get; }

	bool TryExchange(in UiaProbeRequest request, int timeoutMs, out UiaProbeResponse response, out string failure);

	int? Terminate();
}
