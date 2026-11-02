using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DesktopUpdates;

internal sealed class UpdateFeed
{
	private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45.0);

	private readonly UpdateManager _manager;

	private readonly FeedDownloader _downloader;

	public long BytesReceived => _downloader.BytesReceived;

	public bool LastDownloadWasDelta => _downloader.LastBatchWasDelta;

	public VelopackAsset? Staged => _manager.UpdatePendingRestart;

	private UpdateFeed(UpdateManager manager, FeedDownloader downloader)
	{
		_manager = manager;
		_downloader = downloader;
	}

	public static UpdateFeed? Create(UpdateOptions options, UpdateLog log, out string disabledReason)
	{
		if (!options.FeedEnabled)
		{
			disabledReason = options.FeedDisabledReason;
			return null;
		}
		string feedUrl = options.FeedUrl;
		if (string.IsNullOrWhiteSpace(feedUrl))
		{
			disabledReason = "no_feed_url";
			return null;
		}
		try
		{
			FeedDownloader downloader = new FeedDownloader(feedUrl, options.CreateHttpHandler, options.RegisterFeedHost, log);
			UpdateManager updateManager = new UpdateManager(new SimpleWebSource(feedUrl, downloader, 5.0));
			if (!updateManager.IsInstalled)
			{
				disabledReason = "not_installed";
				return null;
			}
			disabledReason = "";
			return new UpdateFeed(updateManager, downloader);
		}
		catch (Exception ex)
		{
			disabledReason = "init_failed:" + ex.GetType().Name;
			return null;
		}
	}

	public Task<UpdateInfo?> CheckAsync(CancellationToken ct)
	{
		return _manager.CheckForUpdatesAsync().WaitAsync(CheckTimeout, ct);
	}

	public Task DownloadAsync(UpdateInfo info, CancellationToken ct)
	{
		_downloader.BeginBatch();
		return _manager.DownloadUpdatesAsync(info, null, ct);
	}

	public bool IsStaged(UpdateInfo info)
	{
		VelopackAsset staged = Staged;
		if ((object)staged != null)
		{
			return staged.Version == info.TargetFullRelease.Version;
		}
		return false;
	}

	public void InstallAndRelaunch(VelopackAsset asset, string[]? restartArgs)
	{
		_manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true, restartArgs);
	}

	public void InstallOnExit(VelopackAsset asset)
	{
		_manager.WaitExitThenApplyUpdates(asset, silent: true, restart: false);
	}
}
