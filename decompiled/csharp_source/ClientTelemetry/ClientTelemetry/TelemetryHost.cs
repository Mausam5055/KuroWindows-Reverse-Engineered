using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClientTelemetry;

public sealed record TelemetryHost(string AppName, string AppVersion, string Platform, string StateDirectory, Uri IngestUrl, Func<CancellationToken, ValueTask<string?>> AccessToken)
{
	public bool SendEnabled { get; init; } = true;

	public string? OsVersion { get; init; }

	public string? OsBuild { get; init; }

	public string? Arch { get; init; }

	public Action<string>? LocalLog { get; init; }

	public ITelemetryClock? Clock { get; init; }

	public ITransport? Transport { get; init; }

	public HttpMessageHandler? HttpHandler { get; init; }
}
