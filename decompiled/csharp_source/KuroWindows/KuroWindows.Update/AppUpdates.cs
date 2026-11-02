using System;
using System.IO;
using DesktopUpdates;
using KuroWindows.Auth;
using KuroWindows.Net;

namespace KuroWindows.Update;

internal static class AppUpdates
{
	private static volatile UpdateService? _service;

	private static UpdateOptions Options => new UpdateOptions
	{
		AppVersion = AppConfig.AppVersion,
		FeedUrl = AppConfig.UpdateFeedUrl,
		LocalDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName),
		CheckVersionAsync = APIClient.CheckVersionResultAsync,
		CreateHttpHandler = ResilientDns.CreateHandler,
		RegisterFeedHost = ResilientDns.RegisterEndpointHost,
		Log = (string message, string member, string file, int line) =>
		{
			KuroLog.Update(message, member, file, line);
		},
		LocalLog = (string message, string member, string file, int line) =>
		{
			KuroLog.LocalOnly("update", message, member, file, line);
		},
		ExceptionSummary = KuroLog.ExceptionSummary
	};

	public static bool IsBelowFloor => _service?.Floor.IsBelowFloor ?? false;

	public static UpdateService Create(IUpdateHost host)
	{
		return _service = new UpdateService(Options, host);
	}

	public static void ReportPreviousRun(string? priorVersion, string? priorExitReason)
	{
		UpdateService.ReportPreviousRun(Options, priorVersion, priorExitReason);
	}

	public static void Heard(string operation, string? responseBody)
	{
		_service?.Floor.Heard(operation, responseBody);
	}
}
