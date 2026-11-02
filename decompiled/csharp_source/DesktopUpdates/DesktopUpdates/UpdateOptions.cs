using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace DesktopUpdates;

public sealed class UpdateOptions
{
	public required string AppVersion { get; init; }

	public string? FeedUrl { get; init; }

	public bool FeedEnabled { get; init; } = true;

	public string FeedDisabledReason { get; init; } = "disabled";

	public required string LocalDataFolder { get; init; }

	public required Func<Task<(UpdateCheckStatus Status, string? ErrorMessage)>> CheckVersionAsync { get; init; }

	public required Func<HttpMessageHandler> CreateHttpHandler { get; init; }

	public Action<string>? RegisterFeedHost { get; init; }

	public required UpdateLogSink Log { get; init; }

	public required UpdateLogSink LocalLog { get; init; }

	public required Func<Exception, string> ExceptionSummary { get; init; }
}
