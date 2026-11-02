using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using KuroWindows.CoreBridge;
using KuroWindows.Diagnostics;
using KuroWindows.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KuroWindows.UI;

public partial class ShellWindow : Window, IComponentConnector
{
	private struct FlashWindowInfo
	{
		public uint cbSize;

		public nint hwnd;

		public uint dwFlags;

		public uint uCount;

		public uint dwTimeout;
	}

	private const string UiHost = "kuro-app.example";

	private const double SoloWindowWidth = 600.0;

	private const double SoloWindowHeight = 400.0;

	private const double DashboardWindowWidth = 800.0;

	private const double DashboardWindowHeight = 600.0;

	private const int WmNcLButtonDown = 161;

	private const int HtCaption = 2;

	private const int SwRestore = 9;

	private const int SwShow = 5;

	private static readonly nint HwndTopmost = new IntPtr(-1);

	private static readonly nint HwndNotopmost = new IntPtr(-2);

	private const uint SwpNoSize = 1u;

	private const uint SwpNoMove = 2u;

	private const uint SwpShowWindow = 64u;

	private static readonly Uri UiUri = new Uri("https://kuro-app.example/index.html");

	private readonly CoreBridgeServer _bridge;

	private readonly Action _requestForegroundSync;

	private bool _allowClose;

	private DateTimeOffset _lastForegroundSyncRequestedAt = DateTimeOffset.MinValue;

	private bool _covered = true;

	private bool _contentionTopmost;

	private bool _contentionRaiseInProgress;

	private const int MaxAttemptsPerCycle = 2;

	private const int MaxBrowserExitRecoveries = 3;

	private const string StoppedMessage = "The dashboard stopped. Click Try again to reopen it.";

	private const string RepairingMessage = "Fixing a Windows component Kuro needs. This can take a few minutes…";

	private WebView2 _webView;

	private bool _uiReady;

	private bool _initInProgress;

	private bool _failedSinceLastSuccess;

	private bool _useAltUserDataDir;

	private int _browserExitRecoveries;

	private static readonly System.Windows.Media.Color DarkSurface = System.Windows.Media.Color.FromRgb(19, 18, 16);

	private static readonly SolidColorBrush DarkSurfaceBrush = CreateDarkSurfaceBrush();

	private const int MaxRendererReloadAttempts = 3;

	private int _rendererReloadAttempts;

	private long _lastRendererReloadTicks;

	private bool _processFailureLoggedSinceSuccess;

	private bool _viewportDiagnosticLogged;

	private const uint FlashwTray = 2u;

	private const uint FlashwTimerNoFg = 12u;

	private static string UiDir => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "ui");

	public ShellWindow(CoreBridgeServer bridge, Action? requestForegroundSync = null)
	{
		_bridge = bridge;
		_requestForegroundSync = requestForegroundSync ?? new Action(Noop);
		InitializeComponent();
		if (UserSettings.Current.Theme == "dark")
		{
			Background = DarkSurfaceBrush;
		}
		ClampToWorkArea();
		_webView = CreateWebViewElement();
		SyncWebViewVisibility();
		Loaded += OnLoaded;
		IsVisibleChanged += OnWindowVisibilityChanged;
		Activated += OnActivationChanged;
		Deactivated += OnActivationChanged;
		Activated += OnForegroundSyncStateChanged;
		Closing += OnClosing;
		Closed += OnClosed;
		_bridge.EventReady += OnBridgeEventReady;
	}

	public void ShowAndFocus()
	{
		ShowAndFocusCore();
	}

	public bool RaiseForCaptureContention()
	{
		_contentionTopmost = true;
		_contentionRaiseInProgress = true;
		bool flag;
		try
		{
			flag = ShowAndFocusCore();
		}
		finally
		{
			_contentionRaiseInProgress = false;
		}
		if (flag)
		{
			ReleaseContentionTopmost("in_front");
		}
		else
		{
			FlashTaskbarButton();
		}
		return flag;
	}

	private bool ShowAndFocusCore()
	{
		KuroLog.LocalOnly("app", $"ShowAndFocus requested visible={IsVisible} state={WindowState}", "ShowAndFocusCore", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 138);
		if (!IsVisible)
		{
			Show();
		}
		if (WindowState == WindowState.Minimized)
		{
			WindowState = WindowState.Normal;
		}
		Activate();
		Topmost = true;
		if (!_contentionTopmost)
		{
			Topmost = false;
		}
		Focus();
		bool result = TryForceForeground();
		RequestForegroundSync("foreground_shown");
		if (_uiReady)
		{
			_bridge.SendSnapshot();
		}
		if (!_uiReady)
		{
			TryInitializeAsync("shown", userInitiated: false);
		}
		return result;
	}

	public void CloseForShutdown()
	{
		_allowClose = true;
		Close();
	}

	private void ClampToWorkArea()
	{
		ApplyWindowSize(Width, Height);
	}

	private void ApplyWindowSize(double requestedWidth, double requestedHeight)
	{
		try
		{
			Rect workArea = SystemParameters.WorkArea;
			MaxWidth = workArea.Width;
			MaxHeight = workArea.Height;
			Width = Math.Min(requestedWidth, workArea.Width);
			Height = Math.Min(requestedHeight, workArea.Height);
			WindowStartupLocation = WindowStartupLocation.Manual;
			Left = workArea.Left + Math.Max(0.0, (workArea.Width - Width) / 2.0);
			Top = workArea.Top + Math.Max(0.0, (workArea.Height - Height) / 2.0);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("app", "Work-area clamp failed: " + ex.Message, "ApplyWindowSize", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 214);
		}
	}

	private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (!IsVisible)
		{
			ReleaseContentionTopmost("hidden");
		}
		SyncWebViewVisibility();
	}

	private void OnActivationChanged(object? sender, EventArgs e)
	{
		if (IsActive && !_contentionRaiseInProgress)
		{
			ReleaseContentionTopmost("activated");
		}
		CoverFollows();
	}

	private void ReleaseContentionTopmost(string reason)
	{
		if (_contentionTopmost)
		{
			_contentionTopmost = false;
			try
			{
				Topmost = false;
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("app", "capture contention topmost release failed: " + ex.Message, "ReleaseContentionTopmost", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 254);
			}
			KuroLog.LocalOnly("app", "capture contention topmost released reason=" + reason, "ReleaseContentionTopmost", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 257);
		}
	}

	private static SolidColorBrush CreateDarkSurfaceBrush()
	{
		SolidColorBrush solidColorBrush = new SolidColorBrush(DarkSurface);
		solidColorBrush.Freeze();
		return solidColorBrush;
	}

	private void CoverFollows()
	{
		bool flag = !IsActive && !_bridge.IsCaptureContended;
		if (flag)
		{
			Cover.Background = ((UserSettings.Current.Theme == "dark") ? DarkSurfaceBrush : System.Windows.Media.Brushes.White);
		}
		Cover.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		SyncWebViewVisibility();
		if (flag != _covered)
		{
			_covered = flag;
			KuroLog.LocalOnly("app", $"Cover {(flag ? "shown" : "hidden")} visible={IsVisible} state={WindowState}", "CoverFollows", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 310);
		}
	}

	private void SyncWebViewVisibility()
	{
		if (_allowClose)
		{
			return;
		}
		bool flag = _uiReady && FallbackPane.Visibility != Visibility.Visible && IsVisible && (IsActive || _bridge.IsCaptureContended);
		try
		{
			_webView.Visibility = ((!flag) ? Visibility.Hidden : Visibility.Visible);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("app", "WebView2 visibility sync failed: " + ex.Message, "SyncWebViewVisibility", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 348);
		}
	}

	public void CaptureContentionChanged()
	{
		if (!_bridge.IsCaptureContended)
		{
			ReleaseContentionTopmost("cleared");
		}
		CoverFollows();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		TryInitializeAsync("loaded", userInitiated: false);
	}

	private async Task TryInitializeAsync(string trigger, bool userInitiated)
	{
		if (_uiReady || _initInProgress || _allowClose)
		{
			return;
		}
		_initInProgress = true;
		try
		{
			string path = Path.Combine(UiDir, "index.html");
			if (!File.Exists(path))
			{
				if (!_failedSinceLastSuccess)
				{
					_failedSinceLastSuccess = true;
					KuroLog.App("WebView2 init failed: UI asset bundle missing file=" + Path.GetFileName(path), "TryInitializeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 397);
				}
				ShowFallback("A Kuro file is missing. Please reinstall Kuro.", showRetry: false);
				return;
			}
			WebView2FailureClass? webView2FailureClass = await AttemptCycleAsync(trigger).ConfigureAwait(continueOnCapturedContext: true);
			if (!webView2FailureClass.HasValue || _allowClose)
			{
				return;
			}
			Task<bool> task = null;
			if (webView2FailureClass == WebView2FailureClass.RuntimeMissing && !userInitiated)
			{
				ShowFallback("Fixing a Windows component Kuro needs. This can take a few minutes…", showRetry: false);
				task = WebView2Health.TryAutoRepairAsync();
				goto IL_01ab;
			}
			bool flag = userInitiated;
			bool flag2;
			if (flag)
			{
				if (webView2FailureClass.HasValue)
				{
					WebView2FailureClass valueOrDefault = webView2FailureClass.GetValueOrDefault();
					if ((uint)valueOrDefault <= 1u)
					{
						flag2 = true;
						goto IL_0190;
					}
				}
				flag2 = false;
				goto IL_0190;
			}
			goto IL_0194;
			IL_0190:
			flag = flag2;
			goto IL_0194;
			IL_01ab:
			if (task != null)
			{
				bool flag3 = await task.ConfigureAwait(continueOnCapturedContext: true);
				if (_allowClose)
				{
					return;
				}
				flag = flag3;
				if (flag)
				{
					flag = !(await AttemptCycleAsync(trigger + "_post_repair").ConfigureAwait(continueOnCapturedContext: true)).HasValue;
				}
				if (flag)
				{
					return;
				}
			}
			ShowFallback("The dashboard could not open. Recording still works. Restart your computer, then click Try again.", showRetry: true);
			return;
			IL_0194:
			if (flag)
			{
				ShowFallback("Fixing a Windows component Kuro needs. This can take a few minutes…", showRetry: false);
				task = WebView2Health.TryUserRepairAsync();
			}
			goto IL_01ab;
		}
		finally
		{
			_initInProgress = false;
		}
	}

	private async Task<WebView2FailureClass?> AttemptCycleAsync(string trigger)
	{
		WebView2FailureClass? failure = null;
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			try
			{
				await AttemptInitOnceAsync().ConfigureAwait(continueOnCapturedContext: true);
				_uiReady = true;
				FallbackPane.Visibility = Visibility.Collapsed;
				SyncWebViewVisibility();
				if (_failedSinceLastSuccess)
				{
					_failedSinceLastSuccess = false;
					KuroLog.App($"WebView2 recovered trigger={trigger} alt_udf={_useAltUserDataDir}", "AttemptCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 470);
				}
				return null;
			}
			catch (Exception ex)
			{
				failure = WebView2Health.Classify(ex);
				if (!_failedSinceLastSuccess)
				{
					_failedSinceLastSuccess = true;
					KuroLog.App($"WebView2 initialization failed class={failure} trigger={trigger} attempt={attempt}/{2} ({WebView2Health.Describe(WebView2Health.Probe())}): {ex}", "AttemptCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 484);
				}
				else
				{
					KuroLog.LocalOnly("app", $"WebView2 initialization failed class={failure} trigger={trigger} attempt={attempt}/{2}: {ex}", "AttemptCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 489);
				}
				if (_allowClose)
				{
					return failure;
				}
				ReplaceWebViewElement();
				if (failure == WebView2FailureClass.UserDataFolder && !_useAltUserDataDir)
				{
					_useAltUserDataDir = true;
				}
				if (attempt < 2)
				{
					await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(continueOnCapturedContext: true);
				}
			}
		}
		return failure;
	}

	private async Task AttemptInitOnceAsync()
	{
		string uiDir = UiDir;
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, _useAltUserDataDir ? "webview2-alt" : "webview2");
		KuroLog.LocalOnly("app", $"WebView2 initializing alt_udf={_useAltUserDataDir}", "AttemptInitOnceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 531);
		Directory.CreateDirectory(text);
		CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, text).ConfigureAwait(continueOnCapturedContext: true);
		await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(continueOnCapturedContext: true);
		_webView.ZoomFactor = 1.0;
		CoreWebView2 coreWebView = _webView.CoreWebView2;
		coreWebView.Settings.AreDefaultContextMenusEnabled = false;
		coreWebView.Settings.AreDevToolsEnabled = false;
		coreWebView.Settings.AreBrowserAcceleratorKeysEnabled = false;
		coreWebView.Settings.IsZoomControlEnabled = false;
		coreWebView.Settings.IsPinchZoomEnabled = false;
		coreWebView.Settings.IsStatusBarEnabled = false;
		_webView.ZoomFactorChanged += OnZoomFactorChanged;
		coreWebView.WebMessageReceived += OnWebMessageReceived;
		coreWebView.NavigationCompleted += OnNavigationCompleted;
		coreWebView.ProcessFailed += OnProcessFailed;
		coreWebView.SetVirtualHostNameToFolderMapping("kuro-app.example", uiDir, CoreWebView2HostResourceAccessKind.DenyCors);
		_webView.Source = UiUri;
	}

	private WebView2 CreateWebViewElement()
	{
		WebView2 webView = new WebView2
		{
			DefaultBackgroundColor = ((UserSettings.Current.Theme == "dark") ? System.Drawing.Color.FromArgb(DarkSurface.R, DarkSurface.G, DarkSurface.B) : System.Drawing.Color.White)
		};
		RootGrid.Children.Insert(0, webView);
		return webView;
	}

	private void ReplaceWebViewElement()
	{
		DetachCoreHandlers();
		RootGrid.Children.Remove(_webView);
		try
		{
			_webView.Dispose();
		}
		catch
		{
		}
		_webView = CreateWebViewElement();
		SyncWebViewVisibility();
	}

	private void DetachCoreHandlers()
	{
		try
		{
			CoreWebView2 coreWebView = _webView.CoreWebView2;
			if (coreWebView != null)
			{
				coreWebView.WebMessageReceived -= OnWebMessageReceived;
				coreWebView.NavigationCompleted -= OnNavigationCompleted;
				coreWebView.ProcessFailed -= OnProcessFailed;
			}
		}
		catch
		{
		}
	}

	private void OnFallbackRetryClick(object sender, RoutedEventArgs e)
	{
		if (_initInProgress)
		{
			return;
		}
		if (_uiReady)
		{
			try
			{
				_webView.CoreWebView2?.Reload();
				return;
			}
			catch
			{
				_uiReady = false;
				ReplaceWebViewElement();
			}
		}
		TryInitializeAsync("retry_button", userInitiated: true);
	}

	private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
	{
		if (!e.IsSuccess)
		{
			KuroLog.App($"WebView2 navigation failed status={e.WebErrorStatus}", "OnNavigationCompleted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 658);
			ShowFallback("The dashboard could not load. Click Try again.", showRetry: true);
			return;
		}
		KuroLog.LocalOnly("app", "WebView2 navigation completed", "OnNavigationCompleted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 663);
		_rendererReloadAttempts = 0;
		_browserExitRecoveries = 0;
		_processFailureLoggedSinceSuccess = false;
		FallbackPane.Visibility = Visibility.Collapsed;
		SyncWebViewVisibility();
		_bridge.SendSnapshot();
		RequestForegroundSync("navigation_completed");
		EnsureNavigationZoomReset();
		if (!_viewportDiagnosticLogged)
		{
			_viewportDiagnosticLogged = true;
			LogViewportScaleDiagnosticAsync();
		}
	}

	private void OnZoomFactorChanged(object? sender, EventArgs e)
	{
		try
		{
			double zoomFactor = _webView.ZoomFactor;
			if (Math.Abs(zoomFactor - 1.0) > 0.001)
			{
				KuroLog.App($"WebView2 zoom factor {zoomFactor:0.###} changed at runtime — reset to 1.0", "OnZoomFactorChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 700);
				_webView.ZoomFactor = 1.0;
			}
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("app", "WebView2 runtime zoom reset failed: " + ex.Message, "OnZoomFactorChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 706);
		}
	}

	private void EnsureNavigationZoomReset()
	{
		try
		{
			double zoomFactor = _webView.ZoomFactor;
			if (Math.Abs(zoomFactor - 1.0) > 0.001)
			{
				KuroLog.App($"WebView2 zoom factor {zoomFactor:0.###} after navigation — reset to 1.0", "EnsureNavigationZoomReset", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 723);
				_webView.ZoomFactor = 1.0;
			}
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("app", "WebView2 zoom reset failed: " + ex.Message, "EnsureNavigationZoomReset", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 729);
		}
	}

	private async Task LogViewportScaleDiagnosticAsync()
	{
		try
		{
			string text = ((await _webView.CoreWebView2.ExecuteScriptAsync("window.innerWidth + 'x' + window.innerHeight + '@' + window.devicePixelRatio").ConfigureAwait(continueOnCapturedContext: true)) ?? "").Trim('"');
			string msg = $"WebView2 viewport diagnostic window_dip={ActualWidth:0}x{ActualHeight:0} css_viewport={text} zoom_factor={_webView.ZoomFactor:0.###}";
			if (int.TryParse(text.Split('@')[0].Split('x')[0], out var result) && result > 0 && ActualWidth > 0.0 && Math.Abs(ActualWidth / (double)result - 1.0) > 0.02)
			{
				KuroLog.App(msg, "LogViewportScaleDiagnosticAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 757);
			}
			else
			{
				KuroLog.LocalOnly("app", msg, "LogViewportScaleDiagnosticAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 761);
			}
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("app", "WebView2 viewport diagnostic failed: " + ex.Message, "LogViewportScaleDiagnosticAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 766);
		}
	}

	private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
	{
		string msg = $"WebView2 process failed kind={e.ProcessFailedKind} reason={e.Reason}";
		CoreWebView2ProcessFailedKind processFailedKind = e.ProcessFailedKind;
		bool flag = (uint)processFailedKind <= 2u;
		if (flag && !_processFailureLoggedSinceSuccess)
		{
			_processFailureLoggedSinceSuccess = true;
			KuroLog.App(msg, "OnProcessFailed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 783);
		}
		else
		{
			KuroLog.LocalOnly("app", msg, "OnProcessFailed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 787);
		}
		switch (e.ProcessFailedKind)
		{
		case CoreWebView2ProcessFailedKind.BrowserProcessExited:
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (!_initInProgress && !_allowClose)
				{
					_uiReady = false;
					ReplaceWebViewElement();
					if (_browserExitRecoveries < 3)
					{
						_browserExitRecoveries++;
						TryInitializeAsync("browser_process_exited", userInitiated: false);
					}
					else
					{
						ShowFallback("The dashboard stopped. Click Try again to reopen it.", showRetry: true);
					}
				}
			}));
			break;
		case CoreWebView2ProcessFailedKind.RenderProcessExited:
		case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
		{
			long timestamp = Stopwatch.GetTimestamp();
			if (timestamp - _lastRendererReloadTicks < Stopwatch.Frequency * 10)
			{
				break;
			}
			if (_rendererReloadAttempts < 3)
			{
				_rendererReloadAttempts++;
				_lastRendererReloadTicks = timestamp;
				KuroLog.App($"WebView2 renderer recovery: reload attempt {_rendererReloadAttempts}/{3}", "OnProcessFailed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 836);
				try
				{
					_webView.Reload();
					break;
				}
				catch (Exception ex)
				{
					KuroLog.App("WebView2 renderer reload failed: " + ex.Message, "OnProcessFailed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 843);
					ShowFallback("The dashboard stopped. Click Try again to reopen it.", showRetry: true);
					break;
				}
			}
			ShowFallback("The dashboard stopped. Click Try again to reopen it.", showRetry: true);
			break;
		}
		}
	}

	private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		CoreBridgeRequest coreBridgeRequest;
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(e.WebMessageAsJson);
			JsonElement rootElement = jsonDocument.RootElement;
			coreBridgeRequest = new CoreBridgeRequest
			{
				Id = ((rootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String) ? (value.GetString() ?? Guid.NewGuid().ToString("N")) : Guid.NewGuid().ToString("N")),
				Method = ((rootElement.TryGetProperty("method", out var value2) && value2.ValueKind == JsonValueKind.String) ? (value2.GetString() ?? "") : ""),
				Params = ((rootElement.TryGetProperty("params", out var value3) && value3.ValueKind != JsonValueKind.Null) ? new JsonElement?(value3.Clone()) : ((JsonElement?)null))
			};
			if (!string.Equals(coreBridgeRequest.Method, "begin_window_drag", StringComparison.OrdinalIgnoreCase))
			{
				KuroLog.LocalOnly("app", "WebView2 message received method=" + coreBridgeRequest.Method + " id=" + coreBridgeRequest.Id, "OnWebMessageReceived", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 881);
			}
		}
		catch (Exception value4)
		{
			KuroLog.App($"WebView2 message parse failed: {value4}", "OnWebMessageReceived", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 886);
			PostResponse(CoreBridgeResponse.Failure(null, "invalid_message", "The desktop UI sent an invalid message."));
			return;
		}
		PostResponse(await ExecuteHostCommandAsync(coreBridgeRequest).ConfigureAwait(continueOnCapturedContext: true));
	}

	private async Task<CoreBridgeResponse> ExecuteHostCommandAsync(CoreBridgeRequest request)
	{
		try
		{
			if (string.Equals(request.Method.Trim(), "present_guard_notice", StringComparison.OrdinalIgnoreCase) && (!IsVisible || !IsActive || WindowState == WindowState.Minimized))
			{
				return CoreBridgeResponse.Failure(request.Id, "notice_not_visible", "The notice is not visible.");
			}
			switch (request.Method.Trim().ToLowerInvariant())
			{
			case "hide_window":
				Hide();
				return CoreBridgeResponse.Success(request.Id);
			case "begin_window_drag":
				BeginWindowDrag();
				return CoreBridgeResponse.Success(request.Id);
			case "resize_window":
				ResizeWindow(request);
				return CoreBridgeResponse.Success(request.Id);
			case "open_external_url":
				OpenExternalUrl(request);
				return CoreBridgeResponse.Success(request.Id);
			default:
				return await Task.Run(() => _bridge.ExecuteAsync(request)).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		catch (Exception ex)
		{
			KuroLog.App($"WebView2 command failed method={request.Method ?? "-"} error={ex}", "ExecuteHostCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 927);
			string code = ((ex is CoreBridgeCommandException ex2) ? ex2.Code : "command_failed");
			return CoreBridgeResponse.Failure(request.Id, code, ex.Message);
		}
	}

	private void ResizeWindow(CoreBridgeRequest request)
	{
		JsonElement? jsonElement = request.Params;
		string text = (((jsonElement.HasValue && jsonElement.GetValueOrDefault().TryGetProperty("mode", out var value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : null) ?? "").Trim().ToLowerInvariant();
		if (!(text == "solo"))
		{
			if (!(text == "dashboard"))
			{
				throw new CoreBridgeCommandException("invalid_window_mode", "Unknown window mode.");
			}
			ApplyWindowSize(800.0, 600.0);
		}
		else
		{
			ApplyWindowSize(600.0, 400.0);
		}
	}

	private void BeginWindowDrag()
	{
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				ReleaseCapture();
				SendMessage(handle, 161, 2, 0);
			}
		}
		catch (Exception ex)
		{
			KuroLog.App("Window drag failed: " + ex.Message, "BeginWindowDrag", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 970);
		}
	}

	private static void OpenExternalUrl(CoreBridgeRequest request)
	{
		JsonElement? jsonElement = request.Params;
		if (!Uri.TryCreate((jsonElement.HasValue && jsonElement.GetValueOrDefault().TryGetProperty("url", out var value)) ? value.GetString() : null, UriKind.Absolute, out Uri result) || (result.Scheme != Uri.UriSchemeHttps && result.Scheme != Uri.UriSchemeHttp))
		{
			throw new CoreBridgeCommandException("invalid_url", "The link could not be opened.");
		}
		KuroLog.LocalOnly("app", "Opening external URL: " + result.Host, "OpenExternalUrl", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 985);
		Process.Start(new ProcessStartInfo(result.ToString())
		{
			UseShellExecute = true
		});
	}

	private void OnBridgeEventReady(CoreBridgeEvent evt)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			if (evt.Type == "show_window")
			{
				ShowAndFocus();
			}
			else
			{
				if (evt.Type == "open_auth_url" && !string.IsNullOrWhiteSpace(evt.Url))
				{
					try
					{
						Process.Start(new ProcessStartInfo(evt.Url)
						{
							UseShellExecute = true
						});
						return;
					}
					catch (Exception ex)
					{
						KuroLog.App("Opening auth URL failed: " + ex.Message, "OnBridgeEventReady", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1007);
						return;
					}
				}
				PostEvent(evt);
			}
		}));
	}

	private void PostEvent(CoreBridgeEvent evt)
	{
		string type = evt.Type;
		bool flag = ((type == "communications_changed" || type == "communications_unsupported") ? true : false);
		if (!flag || (evt.Communications.HasValue && _bridge.IsCurrentCommunications(evt.Communications.Value)))
		{
			PostJson(new
			{
				type = "event",
				@event = evt
			});
		}
	}

	private void PostResponse(CoreBridgeResponse response)
	{
		if (response.Result is JsonElement { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("session_generation", out var _) && !_bridge.IsCurrentCommunications(payload))
		{
			response = CoreBridgeResponse.Failure(response.Id, "cancelled", "The request was cancelled.");
		}
		PostJson(new
		{
			type = "response",
			id = response.Id,
			ok = response.Ok,
			result = response.Result,
			error = response.Error
		});
	}

	private void PostJson(object value)
	{
		try
		{
			CoreWebView2 coreWebView = _webView.CoreWebView2;
			if (coreWebView != null)
			{
				string webMessageAsJson = JsonSerializer.Serialize(value, CoreBridgeServer.JsonOptions);
				coreWebView.PostWebMessageAsJson(webMessageAsJson);
			}
		}
		catch (ObjectDisposedException)
		{
			KuroLog.LocalOnly("app", "WebView2 postMessage skipped: control disposed", "PostJson", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1059);
		}
		catch (Exception ex2)
		{
			KuroLog.App("WebView2 postMessage failed: " + ex2.Message, "PostJson", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1063);
		}
	}

	private void ShowFallback(string message, bool showRetry)
	{
		FallbackText.Text = message;
		FallbackRetryButton.Visibility = ((!showRetry) ? Visibility.Collapsed : Visibility.Visible);
		FallbackPane.Visibility = Visibility.Visible;
		SyncWebViewVisibility();
	}

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		if (!_allowClose)
		{
			e.Cancel = true;
			Hide();
		}
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		Activated -= OnForegroundSyncStateChanged;
		Activated -= OnActivationChanged;
		Deactivated -= OnActivationChanged;
		IsVisibleChanged -= OnWindowVisibilityChanged;
		_bridge.EventReady -= OnBridgeEventReady;
		DetachCoreHandlers();
		try
		{
			_webView.Dispose();
		}
		catch
		{
		}
	}

	private void OnForegroundSyncStateChanged(object? sender, EventArgs e)
	{
		if (IsVisible && IsActive && WindowState != WindowState.Minimized)
		{
			RequestForegroundSync("foreground_active");
		}
	}

	private void RequestForegroundSync(string reason)
	{
		try
		{
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			if (!(utcNow - _lastForegroundSyncRequestedAt < TimeSpan.FromSeconds(1.0)))
			{
				_lastForegroundSyncRequestedAt = utcNow;
				_requestForegroundSync();
			}
		}
		catch (Exception ex)
		{
			KuroLog.App("Foreground desktop_sync request failed: " + ex.Message, "RequestForegroundSync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1130);
		}
	}

	private static void Noop()
	{
	}

	private bool TryForceForeground()
	{
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle == IntPtr.Zero)
			{
				return false;
			}
			nint foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == handle)
			{
				KuroLog.LocalOnly("app", "ShowAndFocus foreground attempt completed in_front=true attached=false", "TryForceForeground", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1157);
				return true;
			}
			uint currentThreadId = GetCurrentThreadId();
			uint num = ((foregroundWindow != IntPtr.Zero) ? GetWindowThreadProcessId(foregroundWindow, out var _) : 0u);
			bool flag = false;
			try
			{
				if (num != 0 && num != currentThreadId)
				{
					flag = AttachThreadInput(currentThreadId, num, fAttach: true);
				}
				ShowWindow(handle, (WindowState == WindowState.Minimized) ? 9 : 5);
				SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, 67u);
				if (!_contentionTopmost)
				{
					SetWindowPos(handle, HwndNotopmost, 0, 0, 0, 0, 67u);
				}
				BringWindowToTop(handle);
				SetForegroundWindow(handle);
				SetActiveWindow(handle);
			}
			finally
			{
				if (flag)
				{
					AttachThreadInput(currentThreadId, num, fAttach: false);
				}
			}
			bool flag2 = GetForegroundWindow() == handle;
			KuroLog.LocalOnly("app", "ShowAndFocus foreground attempt completed in_front=" + (flag2 ? "true" : "false") + " attached=" + (flag ? "true" : "false"), "TryForceForeground", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1191);
			return flag2;
		}
		catch (Exception ex)
		{
			KuroLog.App("ShowAndFocus foreground attempt failed: " + ex.Message, "TryForceForeground", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1196);
			return false;
		}
	}

	private void FlashTaskbarButton()
	{
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				FlashWindowInfo info = new FlashWindowInfo
				{
					cbSize = (uint)Marshal.SizeOf<FlashWindowInfo>(),
					hwnd = handle,
					dwFlags = 14u,
					uCount = 0u,
					dwTimeout = 0u
				};
				FlashWindowEx(ref info);
				KuroLog.LocalOnly("app", "ShowAndFocus foreground refused; flashing the taskbar button", "FlashTaskbarButton", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1220);
			}
		}
		catch (Exception ex)
		{
			KuroLog.App("ShowAndFocus taskbar flash failed: " + ex.Message, "FlashTaskbarButton", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\UI\\ShellWindow.xaml.cs", 1224);
		}
	}

	[DllImport("user32.dll")]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll")]
	private static extern nint SendMessage(nint hWnd, int msg, int wParam, int lParam);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint SetActiveWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool FlashWindowEx(ref FlashWindowInfo info);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
