using System;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using SystemAudio;
using ClientTelemetry;
using DesktopStatus;
using DesktopUpdates;
using DeviceIdentity;
using KuroWindows.Auth;
using KuroWindows.Capture;
using KuroWindows.CoreBridge;
using KuroWindows.Dashboard;
using KuroWindows.Diagnostics;
using KuroWindows.Input;
using KuroWindows.Input.Uia;
using KuroWindows.Net;
using KuroWindows.RecorderCore;
using KuroWindows.Settings;
using KuroWindows.Storage;
using KuroWindows.Sync;
using KuroWindows.UI;
using KuroWindows.Update;
using KuroWindows.Upload;

namespace KuroWindows;

public partial class App : System.Windows.Application, IUpdateHost
{
	private Mutex? _mutex;

	private bool _ownsMutex;

	private SystemAvailabilityEvents? _availabilityEvents;

	private SessionCoordinator? _session;

	private CaptureContentionMonitor? _captureContention;

	private DesktopSyncManager? _desktopSyncManager;

	private UploadManager? _uploadManager;

	private UpdateService? _updates;

	private AppCommandServer? _commandServer;

	private CoreBridgeServer? _coreBridge;

	private ShellWindow? _shellWindow;

	private NotifyIcon? _trayIcon;

	private StatusIcons? _statusIcons;

	private int _shutdownPrepared;

	private volatile bool _restoreRefusedByFloor;

	private bool _loggedDesktopSyncDeferredForOnboarding;

	private static readonly TimeSpan TelemetryDrainBudget = TimeSpan.FromSeconds(5.0);

	bool IUpdateHost.IsShuttingDown => Volatile.Read(in _shutdownPrepared) != 0;

	bool IUpdateHost.SignInInFlight => AuthManager.Instance.State == KuroWindows.Auth.AuthState.SigningIn;

	bool IUpdateHost.WindowInFront
	{
		get
		{
			try
			{
				return Dispatcher.Invoke(() =>
				{
					ShellWindow shellWindow = _shellWindow;
					return shellWindow != null && shellWindow.IsVisible && shellWindow.IsActive;
				}, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2.0));
			}
			catch
			{
				return false;
			}
		}
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		bool flag = e.Args.Any((string arg) => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
		CrashDiagnostics.InstallProcessHandlers();
		CrashDiagnostics.InstallDispatcherHandler(this);
		KuroRunStateTracker.MarkMilestone("app_onstartup_begin");
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		base.OnStartup(e);
		if (e.Args.Length >= 2 && e.Args[0] == "--auth-callback")
		{
			KuroLog.Auth("Auth callback received: " + OAuthCallbackServer.DescribeForLog(e.Args[1]), "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 70);
			if (OAuthCallbackServer.IsAuthCallbackUrl(e.Args[1]))
			{
				OAuthCallbackServer.ForwardCallbackToRunningInstance(e.Args[1]);
			}
			else
			{
				KuroLog.Auth("Auth callback rejected: missing code or wrong host; forwarding show-settings command", "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 77);
				AppCommandServer.SendCommandToRunningInstance("show-settings");
			}
			Shutdown();
			return;
		}
		KuroLog.App("OnStartup begin", "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 84);
		HardwareDiagnostics.LogSnapshotInBackground();
		ProcessMemoryDiagnostics.Start();
		_mutex = Program.SingleInstanceMutex;
		_ownsMutex = Program.SingleInstanceMutexCreatedNew;
		if (!_ownsMutex)
		{
			KuroLog.App($"Second instance detected background={flag}; forwarding show command={!flag}", "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 94);
			if (!flag)
			{
				AppCommandServer.SendCommandToRunningInstance("show-settings");
			}
			Shutdown();
			return;
		}
		_updates = AppUpdates.Create(this);
		if (_updates.InstallStagedAtLaunch(flag))
		{
			return;
		}
		DeviceIdentityRuntime.Configure(AppConfig.DeviceIdentity, (string message, string member, string file, int line) =>
		{
			KuroLog.Auth(message, member, file, line);
		});
		DeviceKey.WarmUp();
		TpmEndorsement.WarmUp();
		HardwareFacets.WarmUp();
		_commandServer = new AppCommandServer(HandleAppCommand);
		_commandServer.Start();
		bool urlSchemeRegistered = AppConfig.EnableCustomSchemeFallback && OAuthCallbackServer.RegisterUrlScheme();
		AuthManager.Instance.StartCallbackServer(urlSchemeRegistered);
		UserSettings.Load();
		ResilientDns.Initialize();
		if (!UserSettings.Current.RecordingEnabled)
		{
			KuroLog.App("Recording disabled by persisted user choice; waiting for manual resume", "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 150);
		}
		SessionEnding += OnSessionEnding;
		SystemAvailabilityEvents systemAvailabilityEvents = (_availabilityEvents = new SystemAvailabilityEvents());
		systemAvailabilityEvents.Resumed += OnSystemResumed;
		systemAvailabilityEvents.SessionBecameActive += OnSystemSessionBecameActive;
		KuroLog.App("Creating SessionCoordinator + UploadManager", "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 170);
		StorageManager storage = new StorageManager();
		InterruptedSessionRecovery.Recover(storage);
		_captureContention = new CaptureContentionMonitor();
		_captureContention.ApplyPolicy(DesktopMaskingPolicy.Seeded);
		_captureContention.SetRecordingArmed(CaptureContentionMonitor.ArmedBySettings);
		SessionCoordinator session = null;
		_uploadManager = new UploadManager(storage, AuthManager.Instance, () => (Dir: session?.CurrentSessionDir, Seg: session?.ActiveSegmentIndex));
		session = new SessionCoordinator(AuthManager.Instance, systemAvailabilityEvents, _uploadManager, _captureContention);
		_session = session;
		_session.StateChanged += OnSessionStateChanged;
		_desktopSyncManager = new DesktopSyncManager(AuthManager.Instance);
		_desktopSyncManager.SnapshotChanged += OnDesktopSyncSnapshotChanged;
		_statusIcons = new StatusIcons("Kuro", "kuro.ico", (string line) =>
		{
			KuroLog.App(line, "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 191);
		});
		_coreBridge = new CoreBridgeServer(AuthManager.Instance, _session, _desktopSyncManager, _uploadManager, storage, _updates, new DashboardApiClient(AuthManager.Instance), _captureContention, () =>
		{
			Dispatcher.BeginInvoke((Func<Task>)(() => QuitAsync()));
		});
		_coreBridge.Start();
		_captureContention.Changed += OnCaptureContentionChanged;
		_statusIcons.SetAttention(_captureContention.IsContended);
		CreateTrayIcon();
		KuroTelemetry.SyncIdentity();
		_uploadManager.Start();
		AuthManager.Instance.StateChanged += OnAuthStateChanged;
		_captureContention.SetAuthState(AuthManager.Instance.State);
		_captureContention.Start();
		StartupManager.SyncRegistration();
		if (AuthManager.Instance.IsSignedIn)
		{
			StartDesktopSync();
		}
		else
		{
			RestoreSessionWithRetryAsync();
		}
		try
		{
			RegisterApplicationRestart("--background", 12);
		}
		catch (Exception ex)
		{
			KuroLog.App("RegisterApplicationRestart failed: " + ex.Message, "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 245);
		}
		_updates.Start();
		WebView2Health.CheckAndAutoRepairInBackgroundAsync();
		if (!flag)
		{
			ShowShellWindow();
		}
		else
		{
			ShowBackgroundLaunchNotification();
		}
		KuroLog.App("OnStartup complete", "OnStartup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 267);
		KuroRunStateTracker.MarkMilestone("app_onstartup_complete");
	}

	private void HandleAppCommand(string command)
	{
		KuroLog.LocalOnly("app", "App command dispatch command=" + command, "HandleAppCommand", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 273);
		Dispatcher.BeginInvoke((Action)(() =>
		{
			try
			{
				if (_shutdownPrepared != 0)
				{
					KuroLog.App("App command ignored during shutdown command=" + command, "HandleAppCommand", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 289);
				}
				else if (string.Equals(command, "show-settings", StringComparison.OrdinalIgnoreCase))
				{
					ShowShellWindow();
				}
				else if (string.Equals(command, "quit", StringComparison.OrdinalIgnoreCase))
				{
					QuitAsync();
				}
			}
			catch (Exception value)
			{
				KuroLog.App($"App command handler failed command={command}: {value}", "HandleAppCommand", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 306);
			}
		}));
	}

	private void ShowShellWindow()
	{
		if (_shutdownPrepared != 0)
		{
			KuroLog.App("ShowShellWindow ignored during shutdown", "ShowShellWindow", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 315);
			return;
		}
		if (_coreBridge == null)
		{
			KuroLog.App("ShowShellWindow ignored because CoreBridge is unavailable", "ShowShellWindow", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 321);
			return;
		}
		if (_shellWindow == null)
		{
			_shellWindow = new ShellWindow(_coreBridge, () =>
			{
				_desktopSyncManager?.RequestImmediateSync();
			});
		}
		_statusIcons?.AttachWindow(_shellWindow);
		_shellWindow.ShowAndFocus();
		KuroLog.LocalOnly("app", "Shell window shown", "ShowShellWindow", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 328);
	}

	private bool? RaiseShellWindowForCaptureContention()
	{
		if (_coreBridge == null)
		{
			return null;
		}
		ShellWindow shellWindow = _shellWindow;
		string text;
		if (shellWindow == null)
		{
			text = "created";
		}
		else if (shellWindow.IsVisible)
		{
			if (shellWindow.WindowState == WindowState.Minimized)
			{
				text = "minimized";
			}
			else
			{
				text = (shellWindow.IsActive ? "active" : "behind");
			}
		}
		else
		{
			text = "hidden";
		}
		if (_shellWindow == null)
		{
			_shellWindow = new ShellWindow(_coreBridge, () =>
			{
				_desktopSyncManager?.RequestImmediateSync();
			});
		}
		_statusIcons?.AttachWindow(_shellWindow);
		bool flag = _shellWindow.RaiseForCaptureContention();
		KuroLog.LocalOnly("app", "capture contention raise in_front=" + (flag ? "true" : "false") + " window=" + text, "RaiseShellWindowForCaptureContention", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 353);
		return flag;
	}

	private void CloseShellWindow()
	{
		try
		{
			if (_shellWindow != null)
			{
				_statusIcons?.DetachWindow(_shellWindow);
			}
			_shellWindow?.CloseForShutdown();
			KuroLog.App("Shell window closed for shutdown", "CloseShellWindow", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 366);
		}
		catch
		{
		}
		_shellWindow = null;
	}

	private void CloseShellWindowOnUiThread()
	{
		try
		{
			Dispatcher.Invoke(CloseShellWindow, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5.0));
		}
		catch (Exception ex)
		{
			KuroLog.App("Shell window close before update apply failed: " + ex.Message, "CloseShellWindowOnUiThread", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 384);
		}
	}

	private void CreateTrayIcon()
	{
		if (_trayIcon != null)
		{
			return;
		}
		_trayIcon = new NotifyIcon
		{
			Icon = (_statusIcons?.CurrentTrayIcon ?? SystemIcons.Application),
			Text = "Kuro",
			Visible = true
		};
		_trayIcon.MouseClick += (object? _, MouseEventArgs e) =>
		{
			MouseButtons button = e.Button;
			if ((button == MouseButtons.Left || button == MouseButtons.Right) ? true : false)
			{
				Dispatcher.Invoke(ShowShellWindow);
			}
		};
		_trayIcon.BalloonTipClicked += (object? _, EventArgs _) =>
		{
			Dispatcher.Invoke(ShowShellWindow);
		};
		_statusIcons?.AttachTrayIcon(_trayIcon);
		KuroLog.LocalOnly("app", "Tray icon created", "CreateTrayIcon", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 410);
	}

	private void ShowBackgroundLaunchNotification()
	{
		if (!UserSettings.Current.RecordingEnabled)
		{
			_trayIcon?.ShowBalloonTip(8000, "Kuro", "Kuro is running, but recording is turned off. Click here to resume when you're ready.", ToolTipIcon.Info);
		}
	}

	private void DisposeTrayIcon()
	{
		try
		{
			if (_trayIcon != null)
			{
				_statusIcons?.DetachTrayIcon(_trayIcon);
				_trayIcon.Visible = false;
				_trayIcon.Dispose();
				_trayIcon = null;
			}
		}
		catch
		{
		}
	}

	private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
	{
		KuroTelemetry.OnAuthStateChanged(e.State);
		KuroLog.Auth($"App auth state changed state={e.State} device={AuthManager.Instance.DeviceId ?? "-"}", "OnAuthStateChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 448);
		_captureContention?.SetAuthState(e.State);
		if (e.State == KuroWindows.Auth.AuthState.SignedIn)
		{
			StartDesktopSync();
			if (UserSettings.Current.OnboardingCompleted)
			{
				Dispatcher.Invoke(() =>
				{
					_session?.RequestStart(RecoveryStartInitiator.AuthSignin);
				});
			}
			return;
		}
		KuroWindows.Auth.AuthState state = e.State;
		bool flag = ((state == KuroWindows.Auth.AuthState.SignedOut || (uint)(state - 4) <= 2u) ? true : false);
		bool flag2 = flag;
		_desktopSyncManager?.Stop(flag2);
		if (flag2)
		{
			VideoBitratePolicy.Clear(SegmentEndReasons.Auth(e.State));
			H264WireProfilePolicy.Clear(SegmentEndReasons.Auth(e.State));
			SystemAudioPolicy.Clear(SegmentEndReasons.Auth(e.State));
			RecorderCorePolicy.ClearPolicy();
			UploadRetentionPolicy.Apply(null);
			UploadCompletionRegion.Apply(null);
		}
		_captureContention?.ApplyPolicy(DesktopMaskingPolicy.Seeded);
		Dispatcher.Invoke(() =>
		{
			_session?.ClearDesktopConfig(SegmentEndReasons.Auth(e.State));
		});
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int RegisterApplicationRestart(string pwzCommandline, int dwFlags);

	private async Task RestoreSessionWithRetryAsync()
	{
		int[] delaysSeconds = new int[5] { 30, 60, 120, 300, 600 };
		SemaphoreSlim networkNudge = new SemaphoreSlim(0, 1);
		try
		{
			NetworkAvailabilityChangedEventHandler onNetworkAvailabilityChanged = (object? _, NetworkAvailabilityEventArgs e) =>
			{
				if (!e.IsAvailable)
				{
					return;
				}
				try
				{
					networkNudge.Release();
				}
				catch (SemaphoreFullException)
				{
				}
				catch (ObjectDisposedException)
				{
				}
			};
			NetworkChange.NetworkAvailabilityChanged += onNetworkAvailabilityChanged;
			try
			{
				int attempt = 0;
				while (true)
				{
					await AuthManager.Instance.RestoreSessionAsync().ConfigureAwait(continueOnCapturedContext: false);
					if (AuthManager.Instance.State != KuroWindows.Auth.AuthState.SignedOut || !AuthManager.Instance.LastRestoreFailureWasTransient)
					{
						_restoreRefusedByFloor = AuthManager.Instance.State == KuroWindows.Auth.AuthState.SignedOut && AppUpdates.IsBelowFloor;
						break;
					}
					int num = delaysSeconds[Math.Min(attempt, delaysSeconds.Length - 1)];
					KuroLog.Auth($"restore: transient failure; retrying in {num}s (attempt {attempt + 1})", "RestoreSessionWithRetryAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 528);
					await networkNudge.WaitAsync(TimeSpan.FromSeconds(num)).ConfigureAwait(continueOnCapturedContext: false);
					if (_shutdownPrepared != 0 || AuthManager.Instance.State != KuroWindows.Auth.AuthState.SignedOut)
					{
						break;
					}
					attempt++;
				}
			}
			finally
			{
				NetworkChange.NetworkAvailabilityChanged -= onNetworkAvailabilityChanged;
			}
		}
		finally
		{
			if (networkNudge != null)
			{
				((IDisposable)networkNudge).Dispose();
			}
		}
	}

	private void StartDesktopSync()
	{
		_loggedDesktopSyncDeferredForOnboarding = false;
		KuroLog.Sync("Starting desktop_sync loop and requesting immediate sync", "StartDesktopSync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 546);
		_desktopSyncManager?.Start();
		_desktopSyncManager?.RequestImmediateSync();
	}

	private void OnDesktopSyncSnapshotChanged(object? sender, DesktopSyncSnapshot snapshot)
	{
		Dispatcher.Invoke(() =>
		{
			if (snapshot.Status == "synced" && snapshot.HasConfig)
			{
				KuroTelemetry.ApplyConfig(snapshot.Config);
				VideoBitratePolicy.Apply(snapshot.Config.Video, snapshot.Config.VideoSource);
				H264WireProfilePolicy.Apply(snapshot.Config.FeatureFlags);
				SystemAudioPolicy.Apply(snapshot.Config.FeatureFlags?.SystemAudio);
				RecorderCorePolicy.ApplyPolicy(snapshot.Config.RecorderCore, snapshot.Config.RecorderCoreSource);
				UploadRetentionPolicy.Apply(snapshot.Config.Upload);
				UploadCompletionRegion.Apply(snapshot.Config.Upload);
				_captureContention?.ApplyPolicy(snapshot.Config.Masking);
				if (UserSettings.Current.OnboardingCompleted)
				{
					_loggedDesktopSyncDeferredForOnboarding = false;
					_session?.ApplyDesktopConfig(snapshot.Config);
				}
				else if (!_loggedDesktopSyncDeferredForOnboarding)
				{
					_loggedDesktopSyncDeferredForOnboarding = true;
					KuroLog.Sync("Desktop sync ready; deferring recording start until onboarding is complete", "OnDesktopSyncSnapshotChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 576);
				}
			}
			else if (snapshot.Status == "access_denied")
			{
				VideoBitratePolicy.Clear("desktop_sync_access_denied");
				H264WireProfilePolicy.Clear("desktop_sync_access_denied");
				SystemAudioPolicy.Clear("desktop_sync_access_denied");
				RecorderCorePolicy.ClearPolicy();
				UploadRetentionPolicy.Apply(null);
				UploadCompletionRegion.Apply(null);
				_captureContention?.ApplyPolicy(DesktopMaskingPolicy.Seeded);
				_session?.ClearDesktopConfig("desktop_sync_access_denied");
			}
		});
	}

	private void OnCaptureContentionChanged(bool contended)
	{
		if (_shutdownPrepared != 0)
		{
			return;
		}
		try
		{
			Dispatcher.BeginInvoke((Action)(() =>
			{
				try
				{
					if (_shutdownPrepared == 0)
					{
						bool flag = _captureContention?.IsContended ?? false;
						_statusIcons?.SetAttention(flag);
						if (contended & flag)
						{
							bool? inFront = RaiseShellWindowForCaptureContention();
							(ContentionMatch, string)? tuple = _captureContention?.OnsetMatch;
							if (tuple.HasValue)
							{
								(ContentionMatch, string) valueOrDefault = tuple.GetValueOrDefault();
								Telemetry.Emit(Ev.CaptureContentionStarted(valueOrDefault.Item1, valueOrDefault.Item2, inFront));
							}
						}
						_shellWindow?.CaptureContentionChanged();
					}
				}
				catch (Exception ex2)
				{
					KuroLog.App("Shell window raise for capture contention failed: " + ex2.Message, "OnCaptureContentionChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 638);
				}
			}));
		}
		catch (InvalidOperationException ex)
		{
			KuroLog.App("Shell window raise for capture contention skipped because dispatcher is unavailable: " + ex.Message, "OnCaptureContentionChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 644);
		}
	}

	public async Task QuitAsync()
	{
		KuroLog.App("QuitAsync begin", "QuitAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 650);
		if (Interlocked.Exchange(ref _shutdownPrepared, 1) != 0)
		{
			KuroLog.App("QuitAsync re-entered; forwarding directly to Shutdown", "QuitAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 654);
			Shutdown();
			return;
		}
		_updates?.Shutdown();
		DisposeTrayIcon();
		CloseShellWindow();
		try
		{
			if (_session != null)
			{
				await _session.StopSessionAsync("app_quit").ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		catch (Exception value)
		{
			KuroLog.App($"QuitAsync stop failed: {value}", "QuitAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 670);
		}
		try
		{
			if (_uploadManager != null)
			{
				await Task.Run(() => _uploadManager.DrainAsync(TimeSpan.FromSeconds(20.0)));
			}
		}
		catch (Exception value2)
		{
			KuroLog.Upload($"QuitAsync drain failed: {value2}", "QuitAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 678);
		}
		try
		{
			_uploadManager?.Stop();
		}
		catch
		{
		}
		try
		{
			UiaProbeHost.Shared.Shutdown("app_quit");
		}
		catch
		{
		}
		try
		{
			_updates?.InstallStagedOnQuit();
		}
		catch
		{
		}
		try
		{
			await Task.Run(() =>
			{
				Telemetry.Drain(TelemetryDrainBudget);
			});
		}
		catch (Exception value3)
		{
			KuroLog.App($"QuitAsync telemetry drain failed: {value3}", "QuitAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 694);
		}
		try
		{
			Telemetry.Stop();
		}
		catch
		{
		}
		KuroLog.App("QuitAsync complete; calling Shutdown", "QuitAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 697);
		Shutdown();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		KuroLog.App("OnExit", "OnExit", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 703);
		DisposeTrayIcon();
		CloseShellWindow();
		_coreBridge?.Dispose();
		_coreBridge = null;
		_commandServer?.Dispose();
		_commandServer = null;
		_updates?.Dispose();
		AuthManager.Instance.StateChanged -= OnAuthStateChanged;
		if (_session != null)
		{
			_session.StateChanged -= OnSessionStateChanged;
		}
		if (_desktopSyncManager != null)
		{
			_desktopSyncManager.SnapshotChanged -= OnDesktopSyncSnapshotChanged;
			_desktopSyncManager.Dispose();
			_desktopSyncManager = null;
		}
		_statusIcons?.Dispose();
		_statusIcons = null;
		if (_captureContention != null)
		{
			_captureContention.Changed -= OnCaptureContentionChanged;
			_captureContention.Dispose();
		}
		ProcessMemoryDiagnostics.Stop();
		SessionEnding -= OnSessionEnding;
		PrepareForShutdown("app_quit");
		if (_availabilityEvents != null)
		{
			_availabilityEvents.Resumed -= OnSystemResumed;
			_availabilityEvents.SessionBecameActive -= OnSystemSessionBecameActive;
			_availabilityEvents.Dispose();
			_availabilityEvents = null;
		}
		if (_ownsMutex)
		{
			_mutex?.ReleaseMutex();
		}
		_mutex?.Dispose();
		base.OnExit(e);
	}

	private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
	{
		KuroLog.App($"SessionEnding reason={e.ReasonSessionEnding}", "OnSessionEnding", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 756);
		PrepareForShutdown("app_quit", skipDrains: true);
	}

	private void OnSessionStateChanged(SessionState state)
	{
		if (_shutdownPrepared != 0)
		{
			return;
		}
		try
		{
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (_shutdownPrepared == 0)
				{
					_statusIcons?.SetSession(state == SessionState.Recording, state == SessionState.PrivacyPaused, _session?.ActivePrivacyContext?.Type);
				}
			}));
		}
		catch (InvalidOperationException ex)
		{
			KuroLog.App("Session icon update skipped because dispatcher is unavailable: " + ex.Message, "OnSessionStateChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 784);
		}
	}

	private void OnSystemResumed(bool userPresent)
	{
		KuroLog.LocalOnly("system", "system resumed kind=" + (userPresent ? "user" : "auto") + "; requested immediate desktop sync, upload and update check", "OnSystemResumed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 797);
		_desktopSyncManager?.RequestImmediateSync();
		_uploadManager?.TriggerUpload();
		_updates?.NotifyWake();
	}

	private void OnSystemSessionBecameActive(SessionActiveReason reason)
	{
		KuroLog.LocalOnly("system", "session active reason=" + reason.ToString().ToLowerInvariant() + "; requested immediate desktop sync", "OnSystemSessionBecameActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 805);
		_desktopSyncManager?.RequestImmediateSync();
	}

	private void PrepareForShutdown(string endReason, bool skipDrains = false)
	{
		_updates?.Shutdown();
		if (Interlocked.Exchange(ref _shutdownPrepared, 1) != 0)
		{
			KuroLog.App("PrepareForShutdown ignored; already prepared endReason=" + endReason, "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 815);
			return;
		}
		KuroLog.App($"PrepareForShutdown begin endReason={endReason} skipDrains={skipDrains}", "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 819);
		try
		{
			_session?.StopSessionAsync(endReason).GetAwaiter().GetResult();
		}
		catch (Exception value)
		{
			KuroLog.App($"PrepareForShutdown stop failed: {value}", "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 826);
		}
		if (!skipDrains)
		{
			try
			{
				_uploadManager?.DrainAsync(TimeSpan.FromSeconds(30.0)).GetAwaiter().GetResult();
			}
			catch (Exception value2)
			{
				KuroLog.Upload($"PrepareForShutdown drain failed: {value2}", "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 837);
			}
		}
		try
		{
			_uploadManager?.Stop();
		}
		catch (Exception value3)
		{
			KuroLog.Upload($"PrepareForShutdown upload stop failed: {value3}", "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 847);
		}
		try
		{
			Telemetry.Drain(skipDrains ? TimeSpan.Zero : TelemetryDrainBudget);
			Telemetry.Stop();
		}
		catch (Exception value4)
		{
			KuroLog.App($"PrepareForShutdown telemetry drain failed: {value4}", "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 860);
		}
		try
		{
			UiaProbeHost.Shared.Shutdown(endReason);
		}
		catch
		{
		}
		KuroLog.App("PrepareForShutdown complete endReason=" + endReason, "PrepareForShutdown", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 868);
		KuroRunStateTracker.MarkCleanShutdown(endReason);
	}

	async Task IUpdateHost.EnterBelowFloorAsync()
	{
		SessionCoordinator session = _session;
		if (session != null)
		{
			await (await Dispatcher.InvokeAsync(() => session.EnterUpdateRequiredStateAsync()).Task.ConfigureAwait(continueOnCapturedContext: false)).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	void IUpdateHost.ExitBelowFloor()
	{
		_session?.ExitUpdateRequiredState();
		if (_restoreRefusedByFloor && Volatile.Read(in _shutdownPrepared) == 0 && AuthManager.Instance.State == KuroWindows.Auth.AuthState.SignedOut)
		{
			_restoreRefusedByFloor = false;
			KuroLog.Auth("restore: the version floor no longer applies; restoring the stored session again", "ExitBelowFloor", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 917);
			RestoreSessionWithRetryAsync();
		}
	}

	Task<bool> IUpdateHost.EndRecordingForInstallAsync(bool immediate, CancellationToken ct)
	{
		return _session?.StopForUpdateInstallAsync(immediate, ct) ?? Task.FromResult(result: true);
	}

	void IUpdateHost.ResumeRecordingAfterAbortedInstall()
	{
		if (Volatile.Read(in _shutdownPrepared) == 0)
		{
			_session?.ReleaseUpdateInstallHold();
		}
	}

	async Task IUpdateHost.DrainUploadsAsync(TimeSpan budget)
	{
		UploadManager uploads = _uploadManager;
		if (uploads == null)
		{
			return;
		}
		try
		{
			KuroLog.Upload("update install: draining recording uploads before the install", "DrainUploadsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 947);
			await Task.Run(() => uploads.DrainAsync(budget)).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			KuroLog.Upload("update install drain failed: " + ex.Message, "DrainUploadsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\App.xaml.cs", 952);
		}
	}

	void IUpdateHost.MarkCleanExitForInstall(string exitReason)
	{
		KuroRunStateTracker.MarkCleanShutdown(exitReason);
	}

	void IUpdateHost.MarkExitAborted(string source, Exception cause)
	{
		KuroRunStateTracker.MarkExitAborted(source, cause);
	}

	void IUpdateHost.StopHelperProcessesForInstall(string reason)
	{
		UiaProbeHost.Shared.Shutdown(reason);
	}

	void IUpdateHost.CloseWindowForInstall()
	{
		CloseShellWindowOnUiThread();
	}
}
