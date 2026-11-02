using System;
using System.Linq;
using System.Threading;
using SystemAudio;
using ClientTelemetry;
using KuroWindows.Capture;
using KuroWindows.Diagnostics;
using KuroWindows.Input.Uia;
using KuroWindows.RecorderCore;
using KuroWindows.RecorderCore.Encode;
using KuroWindows.Settings;
using KuroWindows.Sync;
using KuroWindows.Upload;
using NuGet.Versioning;
using Velopack;

namespace KuroWindows;

internal class Program
{
	internal static Mutex? SingleInstanceMutex { get; private set; }

	internal static bool SingleInstanceMutexCreatedNew { get; private set; }

	[STAThread]
	private static void Main(string[] args)
	{
		if (UiaProbeWorker.IsProbeLaunch(args))
		{
			KuroRunStateTracker.Suppress();
			UiaProbeWorker.Run(args);
		}
		else
		{
			RunRecorder(args);
		}
	}

	private static void RunRecorder(string[] args)
	{
		CrashDiagnostics.InstallProcessHandlers();
		CrashDiagnostics.ConfigureWerLocalDumps();
		try
		{
			SystemAudioRuntime.Configure(AppConfig.SystemAudio, (string message, string member, string file, int line) =>
			{
				KuroLog.Capture(message, member, file, line);
			}, (string message, string member, string file, int line) =>
			{
				KuroLog.LocalOnly("capture", message, member, file, line);
			}, () =>
			{
				LeakedWriterCounter.Increment();
			}, () =>
			{
				Count.SystemAudioLatched(1L);
			});
			SeedDesktopConfigFromCache();
			KuroTelemetry.Start(sendEnabled: false);
			VelopackApp.Build().SetAutoApplyOnStartup(autoApply: false).OnBeforeUninstallFastCallback((SemanticVersion _) =>
			{
				StartupManager.RemoveRegistration();
			})
				.Run();
			bool flag = Enumerable.Contains(args, "--auth-callback");
			if (!flag)
			{
				SingleInstanceMutex = new Mutex(initiallyOwned: true, AppConfig.MutexName, out var createdNew);
				SingleInstanceMutexCreatedNew = createdNew;
			}
			if (!flag && SingleInstanceMutexCreatedNew)
			{
				Telemetry.SetSendEnabled(enabled: true);
				PostCrashReporter.ReportPreviousCrashIfAny();
				KuroRunStateTracker.MarkStarted(args);
				KuroRunStateTracker.StartHeartbeat();
			}
			else
			{
				KuroRunStateTracker.Suppress();
			}
			KuroLog.App("Program Main begin", "RunRecorder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Program.cs", 130);
			App app = new App();
			KuroRunStateTracker.MarkMilestone("app_constructed");
			app.InitializeComponent();
			KuroRunStateTracker.MarkMilestone("wpf_initialized");
			app.Run();
			KuroRunStateTracker.MarkCleanShutdown("app_run_returned");
		}
		catch (Exception ex)
		{
			KuroRunStateTracker.MarkFatal("program_main_exception", ex);
			KuroLog.Crash($"FATAL Main: {ex}", "RunRecorder", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Program.cs", 142);
			Telemetry.FlushToDisk();
			throw;
		}
	}

	private static void SeedDesktopConfigFromCache()
	{
		try
		{
			CachedDesktopConfig cachedDesktopConfig = new DesktopConfigCache().Load();
			if (!(cachedDesktopConfig == null))
			{
				KuroTelemetry.ApplyConfig(cachedDesktopConfig.Config);
				VideoBitratePolicy.Apply(cachedDesktopConfig.Config.Video, cachedDesktopConfig.Config.VideoSource);
				H264WireProfilePolicy.Apply(cachedDesktopConfig.Config.FeatureFlags);
				SystemAudioPolicy.Apply(cachedDesktopConfig.Config.FeatureFlags?.SystemAudio);
				RecorderCorePolicy.ApplyPolicy(cachedDesktopConfig.Config.RecorderCore, cachedDesktopConfig.Config.RecorderCoreSource);
				UploadRetentionPolicy.Apply(cachedDesktopConfig.Config.Upload);
				UploadCompletionRegion.Apply(cachedDesktopConfig.Config.Upload);
				DesktopFeatureFlags.Seeded = cachedDesktopConfig.Config.FeatureFlags?.Normalize() ?? DesktopFeatureFlags.Defaults;
				DesktopMaskingPolicy.Seeded = cachedDesktopConfig.Config.Masking;
				KuroLog.LocalOnly("telemetry", $"telemetry seeded from cached desktop config version={cachedDesktopConfig.ConfigVersion} cached_at={cachedDesktopConfig.CachedAt:O} masking_blocked_apps={cachedDesktopConfig.Config.Masking.BlockedApps.Length}", "SeedDesktopConfigFromCache", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Program.cs", 175);
			}
		}
		catch
		{
		}
	}
}
