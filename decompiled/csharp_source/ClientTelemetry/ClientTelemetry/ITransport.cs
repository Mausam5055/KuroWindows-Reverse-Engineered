using System;
using System.Threading;

namespace ClientTelemetry;

public interface ITransport
{
	SendOutcome Send(Uri url, string bearer, ReadOnlyMemory<byte> body, CancellationToken ct);
}
