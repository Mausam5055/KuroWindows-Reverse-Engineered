using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using DesktopUpdates;
using KuroWindows.Auth;
using KuroWindows.Capture;
using KuroWindows.Dashboard;
using KuroWindows.Input;
using KuroWindows.Settings;
using KuroWindows.Storage;
using KuroWindows.Sync;
using KuroWindows.Upload;

namespace KuroWindows.CoreBridge;

public sealed class CoreBridgeServer : IDisposable
{
	internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { (JsonConverter)new JsonStringEnumConverter() }
	};

	private static readonly HashSet<string> QuietLogMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "get_dashboard", "get_communications", "get_referral", "list_days", "list_transactions", "dismiss_notice", "present_guard_notice", "request_payout" };

	private readonly AuthManager _auth;

	private readonly SessionCoordinator _session;

	private readonly DesktopSyncManager _desktopSync;

	private readonly UploadManager _uploadManager;

	private readonly StorageManager _storage;

	private readonly UpdateService _updates;

	private readonly DashboardApiClient _dashboard;

	private readonly CaptureContentionMonitor _captureContention;

	private readonly CommunicationsSession _communications;

	private readonly Action _quitApp;

	private readonly object _dashboardLogLock = new object();

	private string? _lastDashboardErrorKey;

	private string? _lastPayMode;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly object _uploadProgressLock = new object();

	private readonly object _broadcastLock = new object();

	private readonly object _broadcastEmitLock = new object();

	private bool _broadcastPending;

	private UploadProgressInfo? _lastUploadProgress;

	private Timer? _debounceTimer;

	private CancellationTokenSource? _signInCts;

	private volatile bool _signInCancelledByUser;

	private string? _recordingTransition;

	private string? _lastRecordingControlLogKey;

	private bool _disposed;

	public bool IsCaptureContended => _captureContention.IsContended;

	public event Action<CoreBridgeEvent>? EventReady;

	public CoreBridgeServer(AuthManager auth, SessionCoordinator session, DesktopSyncManager desktopSync, UploadManager uploadManager, StorageManager storage, UpdateService updates, DashboardApiClient dashboard, CaptureContentionMonitor captureContention, Action quitApp)
	{
		_auth = auth;
		_session = session;
		_desktopSync = desktopSync;
		_uploadManager = uploadManager;
		_storage = storage;
		_updates = updates;
		_dashboard = dashboard;
		_captureContention = captureContention;
		_communications = new CommunicationsSession(() => (!_auth.IsSignedIn) ? null : _auth.Session?.UserId, (string? revision, CancellationToken ct) => _dashboard.GetCommunicationsAsync(revision, ct), null, (Exception error) => error is APIClientException ex && ex.Code == "unknown_action");
		_communications.Changed += OnCommunicationsChanged;
		_communications.Unsupported += OnCommunicationsUnsupported;
		_quitApp = quitApp;
	}

	public void Start()
	{
		_auth.StateChanged += OnAuthStateChanged;
		_session.StateChanged += OnSessionStateChanged;
		_session.RecordingControlChanged += OnRecordingControlChanged;
		_desktopSync.SnapshotChanged += OnDesktopSyncChanged;
		_desktopSync.CommunicationsHintReceived += OnCommunicationsHint;
		_uploadManager.ProgressChanged += OnUploadProgressChanged;
		_updates.Changed += ScheduleBroadcast;
		_captureContention.Changed += OnCaptureContentionChanged;
		KuroLog.App("CoreBridge: started in-process", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 119);
	}

	public async Task<CoreBridgeResponse> ExecuteAsync(CoreBridgeRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			if (string.IsNullOrWhiteSpace(request.Method))
			{
				return CoreBridgeResponse.Failure(request.Id, "invalid_request", "Missing method.");
			}
			bool quiet = QuietLogMethods.Contains(request.Method.Trim());
			if (!quiet)
			{
				KuroLog.App("CoreBridge: command begin method=" + request.Method, "ExecuteAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 134);
			}
			object result = await ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (!quiet)
			{
				KuroLog.App("CoreBridge: command complete method=" + request.Method, "ExecuteAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 141);
			}
			return CoreBridgeResponse.Success(request.Id, result);
		}
		catch (OperationCanceledException)
		{
			KuroLog.App("CoreBridge: command cancelled method=" + (request.Method ?? "-"), "ExecuteAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 148);
			return CoreBridgeResponse.Failure(request.Id, "cancelled", "The command was cancelled.");
		}
		catch (CoreBridgeCommandException ex2)
		{
			if (!QuietLogMethods.Contains((request.Method ?? "").Trim()))
			{
				KuroLog.App("CoreBridge: command rejected method=" + (request.Method ?? "-") + " rejection_code=" + ex2.Code, "ExecuteAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 158);
			}
			return CoreBridgeResponse.Failure(request.Id, ex2.Code, ex2.Message);
		}
		catch (Exception ex3)
		{
			KuroLog.App("CoreBridge: command error method=" + (request.Method ?? "-") + " error=" + ex3.Message, "ExecuteAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 165);
			return CoreBridgeResponse.Failure(request.Id, "command_failed", ex3.Message);
		}
	}

	public void SendSnapshot()
	{
		BroadcastSnapshot();
	}

	private async Task<object> ExecuteCommandAsync(CoreBridgeRequest request, CancellationToken cancellationToken)
	{
		switch (request.Method.Trim().ToLowerInvariant())
		{
		case "get_snapshot":
			return CreateSnapshot();
		case "start_recording":
			KuroLog.App("CoreBridge: start_recording requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 180);
			EnsureManualRecordingCommandAllowed();
			UserSettings.Current.RecordingEnabled = true;
			UserSettings.Save();
			_captureContention.SetRecordingArmed(CaptureContentionMonitor.ArmedBySettings);
			SetRecordingTransition("starting");
			try
			{
				_session.RequestStart(RecoveryStartInitiator.CorebridgeManual);
			}
			finally
			{
				ClearRecordingTransition();
			}
			ScheduleBroadcast();
			return CreateSnapshot();
		case "stop_recording":
			KuroLog.App("CoreBridge: stop_recording requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 200);
			EnsureManualRecordingCommandAllowed();
			UserSettings.Current.RecordingEnabled = false;
			UserSettings.Save();
			SetRecordingTransition("stopping");
			try
			{
				await _session.StopSessionAsync();
			}
			finally
			{
				_captureContention.SetRecordingArmed(CaptureContentionMonitor.ArmedBySettings);
				ClearRecordingTransition();
			}
			ScheduleBroadcast();
			return CreateSnapshot();
		case "retry_uploads":
			KuroLog.App("CoreBridge: retry_uploads requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 220);
			_uploadManager.TriggerUpload();
			ScheduleBroadcast();
			return CreateSnapshot();
		case "request_sync":
			KuroLog.App("CoreBridge: request_sync requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 226);
			_desktopSync.RequestImmediateSync();
			ScheduleBroadcast();
			return CreateSnapshot();
		case "sign_out":
			KuroLog.App("CoreBridge: sign_out requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 232);
			CancelSignIn();
			_communications.Reset();
			try
			{
				await Task.Run(() =>
				{
					Telemetry.Drain(TimeSpan.FromSeconds(2.0));
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception value3)
			{
				KuroLog.App($"sign-out telemetry drain failed: {value3}", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 239);
			}
			await _auth.SignOutAsync().ConfigureAwait(continueOnCapturedContext: false);
			ScheduleBroadcast();
			return CreateSnapshot();
		case "sign_in":
			KuroLog.App("CoreBridge: sign_in requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 245);
			StartSignInInBackground();
			return CreateSnapshot();
		case "cancel_sign_in":
			KuroLog.App("CoreBridge: cancel_sign_in requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 250);
			CancelSignIn();
			ScheduleBroadcast();
			return CreateSnapshot();
		case "check_updates":
			KuroLog.App("CoreBridge: check_updates requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 257);
			_updates.CheckNow();
			return CreateSnapshot();
		case "install_update":
			KuroLog.App("CoreBridge: install_update requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 263);
			_updates.InstallNow();
			return CreateSnapshot();
		case "get_dashboard":
			return await GetDashboardDataAsync(ReadTimeZone(request), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		case "get_communications":
			EnsureDashboardSignedIn();
			try
			{
				JsonElement value = default;
				return await _communications.RefreshAsync((request.Params?.TryGetProperty("force", out value) ?? false) && value.ValueKind == JsonValueKind.True, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (APIClientException ex2)
			{
				throw MapDashboardError("get_communications", ex2);
			}
			catch
			{
				throw new CoreBridgeCommandException("communications_unavailable", "The request could not be completed.");
			}
		case "get_referral":
			return await RunDashboardReadAsync("get_referral", (CancellationToken ct) => _dashboard.GetReferralAsync(ct), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		case "list_days":
		{
			int offset2 = ReadOffset(request);
			string tz2 = ReadTimeZone(request);
			return await RunDashboardReadAsync("list_days", (CancellationToken ct) => _dashboard.ListDaysAsync(offset2, tz2, ct), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		case "list_transactions":
		{
			int offset = ReadOffset(request);
			string tz = ReadTimeZone(request);
			return await RunDashboardReadAsync("list_transactions", (CancellationToken ct) => _dashboard.ListTransactionsAsync(offset, tz, ct), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		case "request_payout":
			return await RequestPayoutAsync(ReadRequiredString(request, "method"), ReadOptionalString(request, "destination"), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		case "dismiss_notice":
			return DismissNotice(ReadOptionalString(request, "id"));
		case "present_guard_notice":
		{
			string noticeId = ReadRequiredString(request, "notice_id");
			if (!Guid.TryParse(noticeId, out var _))
			{
				throw new CoreBridgeCommandException("invalid_request", "A valid notice id is required.");
			}
			return await RunDashboardReadAsync("present_guard_notice", (CancellationToken ct) => _dashboard.PresentGuardNoticeAsync(noticeId, ReadOptionalString(request, "content_revision"), ct), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		case "quit":
			KuroLog.App("CoreBridge: quit requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 331);
			_quitApp();
			return CreateSnapshot();
		case "set_launch_at_startup":
		{
			JsonElement value2 = default;
			bool flag = (request.Params?.TryGetProperty("enabled", out value2) ?? false) && value2.GetBoolean();
			KuroLog.App($"CoreBridge: set_launch_at_startup enabled={flag}", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 338);
			UserSettings.Current.LaunchAtStartup = flag;
			UserSettings.Save();
			StartupManager.SyncRegistration();
			ScheduleBroadcast();
			return CreateSnapshot();
		}
		case "set_theme":
		{
			string text2 = ((ReadOptionalString(request, "theme") == "dark") ? "dark" : "light");
			KuroLog.App("CoreBridge: set_theme theme=" + text2, "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 351);
			UserSettings.Current.Theme = text2;
			UserSettings.Save();
			return CreateSnapshot();
		}
		case "complete_onboarding":
		{
			KuroLog.App("CoreBridge: complete_onboarding requested", "ExecuteCommandAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 358);
			string text = _auth.Session?.UserId;
			if (text != null && text.Length > 0)
			{
				ConsentRecorder.Instance.Enqueue(text, ConsentRecorder.OnboardingDocuments);
			}
			UserSettings.Current.OnboardingCompleted = true;
			UserSettings.Current.RecordingEnabled = true;
			UserSettings.Save();
			StartupManager.SyncRegistration();
			_captureContention.SetRecordingArmed(CaptureContentionMonitor.ArmedBySettings);
			EnsureRecordingStartedAfterOnboarding();
			ScheduleBroadcast();
			return CreateSnapshot();
		}
		default:
			throw new CoreBridgeCommandException("unknown_method", "Unknown core bridge method '" + request.Method + "'.");
		}
	}

	private async Task<object> GetDashboardDataAsync(string? tz, CancellationToken cancellationToken)
	{
		EnsureDashboardSignedIn();
		JsonElement data;
		try
		{
			data = await _dashboard.GetDashboardAsync(cancellationToken, !_communications.HasEnvelope, tz).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (APIClientException ex)
		{
			throw MapDashboardError("get_dashboard", ex);
		}
		ClearDashboardErrorDedup();
		NotePayMode(data);
		HashSet<string> hashSet = ExtractNoticeIds(data);
		string[] array = UserSettings.Current.DismissedNoticeIds;
		if (hashSet != null)
		{
			string[] array2 = array;
			array = UserSettings.Current.PruneDismissedNotices(hashSet);
			if (array2 != array)
			{
				UserSettings.Save();
			}
		}
		JsonObject jsonObject;
		try
		{
			jsonObject = JsonNode.Parse(data.GetRawText())?.AsObject() ?? new JsonObject();
		}
		catch (Exception ex2)
		{
			KuroLog.App("get_dashboard payload parse failed: " + ex2.GetType().Name, "GetDashboardDataAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 430);
			throw new CoreBridgeCommandException("dashboard_error", "The dashboard could not be loaded.");
		}
		JsonArray jsonArray = new JsonArray();
		string[] array3 = array;
		foreach (string value in array3)
		{
			jsonArray.Add(value);
		}
		jsonObject["dismissed_notice_ids"] = jsonArray;
		return jsonObject;
	}

	private async Task<object> RunDashboardReadAsync(string method, Func<CancellationToken, Task<JsonElement>> call, CancellationToken cancellationToken)
	{
		EnsureDashboardSignedIn();
		try
		{
			JsonElement jsonElement = await call(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			ClearDashboardErrorDedup();
			return jsonElement;
		}
		catch (APIClientException ex)
		{
			throw MapDashboardError(method, ex);
		}
	}

	private async Task<object> RequestPayoutAsync(string method, string? destination, CancellationToken cancellationToken)
	{
		EnsureDashboardSignedIn();
		string text = method.Trim();
		string destination2 = (string.IsNullOrWhiteSpace(destination) ? null : destination.Trim());
		KuroLog.App("request_payout attempt method=" + text, "RequestPayoutAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 474);
		try
		{
			JsonElement jsonElement = await _dashboard.RequestPayoutAsync(text, destination2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			ClearDashboardErrorDedup();
			string text2 = ReadResponseString(jsonElement, "status") ?? "unknown";
			KuroLog.App("request_payout ok outcome=" + text2, "RequestPayoutAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 480);
			return jsonElement;
		}
		catch (APIClientException ex)
		{
			throw MapDashboardError("request_payout", ex);
		}
	}

	private static string? ReadResponseString(JsonElement data, string key)
	{
		if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
		{
			return value.GetString();
		}
		return null;
	}

	private object DismissNotice(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			throw new CoreBridgeCommandException("invalid_request", "A notice id is required.");
		}
		string[] array = UserSettings.Current.AddDismissedNotice(id.Trim());
		UserSettings.Save();
		KuroLog.LocalOnly("app", $"dismiss_notice id={id.Trim()} count={array.Length}", "DismissNotice", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 509);
		return new DismissNoticeResult(array);
	}

	private void EnsureDashboardSignedIn()
	{
		if (!_auth.IsSignedIn)
		{
			throw new CoreBridgeCommandException("unauthorized", "Sign in to view your dashboard.");
		}
	}

	private CoreBridgeCommandException MapDashboardError(string method, APIClientException ex)
	{
		if (ex.IsUpdateRequired)
		{
			LogDashboardError(method, "update_required", ex.StatusCode);
			return new CoreBridgeCommandException("update_required", "An update is required to continue.");
		}
		string code;
		if (string.IsNullOrEmpty(ex.Code))
		{
			code = ((ex.StatusCode == 408) ? "timeout" : "dashboard_error");
		}
		else
		{
			code = ex.Code;
		}
		LogDashboardError(method, code, ex.StatusCode);
		return new CoreBridgeCommandException(code, "The request could not be completed.");
	}

	private void LogDashboardError(string method, string code, int status)
	{
		string text = $"{method}|{code}|{status}";
		lock (_dashboardLogLock)
		{
			if (string.Equals(text, _lastDashboardErrorKey, StringComparison.Ordinal))
			{
				return;
			}
			_lastDashboardErrorKey = text;
		}
		KuroLog.App($"{method} failed code={code} status={status}", "LogDashboardError", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 559);
	}

	private void ClearDashboardErrorDedup()
	{
		lock (_dashboardLogLock)
		{
			_lastDashboardErrorKey = null;
		}
	}

	private static HashSet<string>? ExtractNoticeIds(JsonElement data)
	{
		if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("notices", out var value) || value.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var value2) && value2.ValueKind == JsonValueKind.String)
			{
				string text = value2.GetString();
				if (!string.IsNullOrEmpty(text))
				{
					hashSet.Add(text);
				}
			}
		}
		return hashSet;
	}

	private void NotePayMode(JsonElement data)
	{
		if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("pay", out var value) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("mode", out var value2) || value2.ValueKind != JsonValueKind.String)
		{
			return;
		}
		string text = value2.GetString();
		if (string.IsNullOrEmpty(text) || text.Length > 32)
		{
			return;
		}
		lock (_dashboardLogLock)
		{
			if (text == _lastPayMode)
			{
				return;
			}
			_lastPayMode = text;
		}
		KuroLog.LocalOnly("app", "pay mode=" + text, "NotePayMode", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 619);
	}

	private static string? ReadTimeZone(CoreBridgeRequest request)
	{
		string text = ReadOptionalString(request, "tz");
		if (string.IsNullOrEmpty(text) || text.Length > 64)
		{
			return null;
		}
		string text2 = text;
		foreach (char c in text2)
		{
			if ((c < 'A' || c > 'Z') && (c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '_' && c != '+' && c != '-' && c != '/')
			{
				return null;
			}
		}
		return text;
	}

	private static int ReadOffset(CoreBridgeRequest request)
	{
		JsonElement? jsonElement = request.Params;
		if (jsonElement.HasValue && jsonElement.GetValueOrDefault().TryGetProperty("offset", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var value2) && value2 > 0)
		{
			return value2;
		}
		return 0;
	}

	private static string ReadRequiredString(CoreBridgeRequest request, string key)
	{
		string? text = ReadOptionalString(request, key);
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new CoreBridgeCommandException("invalid_request", "'" + key + "' is required.");
		}
		return text;
	}

	private static string? ReadOptionalString(CoreBridgeRequest request, string key)
	{
		JsonElement? jsonElement = request.Params;
		if (jsonElement.HasValue && jsonElement.GetValueOrDefault().TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
		{
			return value.GetString();
		}
		return null;
	}

	private void EnsureManualRecordingCommandAllowed()
	{
		if (!_auth.IsSignedIn)
		{
			throw new CoreBridgeCommandException("not_signed_in", "Sign in before controlling recording.");
		}
		if (!_session.ManualRecordingControlsAllowed)
		{
			throw new CoreBridgeCommandException("command_not_allowed", "Recording controls are unavailable right now.");
		}
	}

	private void EnsureRecordingStartedAfterOnboarding()
	{
		if (_auth.IsSignedIn)
		{
			_desktopSync.Start();
			_desktopSync.RequestImmediateSync();
			DesktopSyncSnapshot snapshot = _desktopSync.Snapshot;
			if (snapshot.Status == "synced" && snapshot.HasConfig)
			{
				_session.ApplyDesktopConfig(snapshot.Config);
			}
			else
			{
				_session.RequestStart(RecoveryStartInitiator.Onboarding);
			}
		}
	}

	private void StartSignInInBackground()
	{
		KuroWindows.Auth.AuthState state = _auth.State;
		if ((uint)(state - 1) <= 2u)
		{
			KuroLog.App($"CoreBridge: sign_in ignored because auth state={_auth.State}", "StartSignInInBackground", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 709);
			return;
		}
		CancelSignIn();
		_signInCancelledByUser = false;
		CancellationTokenSource signInCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
		signInCts.CancelAfter(TimeSpan.FromSeconds(120.0));
		_signInCts = signInCts;
		Task.Run(async () =>
		{
			try
			{
				await _auth.BeginSignInAsync(OpenUrl, signInCts.Token).ConfigureAwait(continueOnCapturedContext: false);
				BroadcastShowWindow();
			}
			catch (OperationCanceledException)
			{
				if (!_signInCancelledByUser)
				{
					KuroLog.Auth("CoreBridge: sign_in timed out waiting for OAuth callback", "StartSignInInBackground", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 734);
				}
			}
			catch (Exception ex2)
			{
				KuroLog.Auth("CoreBridge: sign_in failed: " + ex2.Message, "StartSignInInBackground", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 739);
				BroadcastShowWindow();
			}
			finally
			{
				if (_signInCts == signInCts)
				{
					_signInCts = null;
				}
				signInCts.Dispose();
				ScheduleBroadcast();
			}
		});
	}

	private void CancelSignIn()
	{
		CancellationTokenSource cancellationTokenSource = Interlocked.Exchange(ref _signInCts, null);
		if (cancellationTokenSource == null)
		{
			return;
		}
		_signInCancelledByUser = true;
		try
		{
			cancellationTokenSource.Cancel();
		}
		catch
		{
		}
	}

	private CoreBridgeSnapshot CreateSnapshot()
	{
		KuroWindows.Auth.AuthState state = _auth.State;
		DesktopSyncSnapshot snapshot = _desktopSync.Snapshot;
		DesktopConfig config = snapshot.Config;
		UploadProgressInfo progress = LastUploadProgress();
		PrivacyContext activePrivacyContext = _session.ActivePrivacyContext;
		StartupRegistrationStatus registrationStatus = StartupManager.GetRegistrationStatus();
		UpdateSnapshot snapshot2 = _updates.Snapshot;
		long num = 0L;
		try
		{
			num = _storage.AvailableDiskSpaceBytes();
		}
		catch
		{
		}
		CoreBridgeSnapshot coreBridgeSnapshot = new CoreBridgeSnapshot
		{
			Auth = new CoreBridgeAuthState
			{
				State = state.ToString(),
				SignedIn = _auth.IsSignedIn,
				Email = _auth.UserEmail,
				UserId = ((!_auth.IsSignedIn) ? null : _auth.Session?.UserId?.ToLowerInvariant()),
				SessionGeneration = (_auth.IsSignedIn ? new int?(_communications.SessionGeneration) : ((int?)null)),
				BanReason = _auth.LastBanReason,
				LinkedEmail = _auth.LastLinkedEmail,
				LastError = _auth.LastSignInError,
				LastErrorCode = _auth.LastSignInErrorCode
			},
			Device = new CoreBridgeDeviceState
			{
				Id = _auth.DeviceId
			},
			DesktopSync = new CoreBridgeDesktopSyncState
			{
				Status = snapshot.Status,
				HasConfig = snapshot.HasConfig,
				ConfigVersion = snapshot.ConfigVersion,
				LastSeenAt = snapshot.LastSeenAt,
				SyncedAt = snapshot.SyncedAt,
				NextSyncAfterSeconds = snapshot.NextSyncAfterSeconds,
				LastError = snapshot.LastError
			},
			Recording = new CoreBridgeRecordingState
			{
				State = _session.State.ToString(),
				Transition = Volatile.Read(in _recordingTransition),
				Expected = _session.RecordingExpected,
				ManualControlsAllowed = _session.ManualRecordingControlsAllowed,
				CurrentSegmentIndex = _session.CurrentSegmentIndex,
				HasActiveSession = (_session.CurrentSessionDir != null),
				UnsupportedTopologyReason = _session.UnsupportedTopologyReason
			},
			Masking = new CoreBridgeMaskingState
			{
				Status = ((_session.State == SessionState.PrivacyPaused) ? "privacy_paused" : "normal"),
				ContextType = activePrivacyContext?.Type,
				ContextDetail = activePrivacyContext?.Detail,
				ContextAppName = activePrivacyContext?.AppName,
				Mode = (snapshot.HasConfig ? config.Masking.Mode : "default"),
				BlockedAppCount = (snapshot.HasConfig ? config.Masking.BlockedApps.Length : 0),
				SensitiveBrowserTitleKeywordCount = (snapshot.HasConfig ? config.Masking.SensitiveBrowserTitleKeywords.Length : 0),
				ExcludedBrowserTitleKeywordCount = (snapshot.HasConfig ? config.Masking.ExcludedBrowserTitleKeywords.Length : 0),
				CaptureContention = _captureContention.IsContended
			},
			Upload = CoreBridgeUploadState.FromProgress(progress),
			Disk = new CoreBridgeDiskState
			{
				AvailableBytes = num,
				LowDisk = (num > 0 && num < AppConfig.MinimumDiskSpaceBytes)
			},
			Update = new CoreBridgeUpdateState
			{
				Status = ((snapshot2.Required || _session.State == SessionState.UpdateRequired) ? "required" : snapshot2.Status),
				Required = (snapshot2.Required || _session.State == SessionState.UpdateRequired),
				AvailableVersion = snapshot2.AvailableVersion,
				LastError = null
			},
			Settings = new CoreBridgeSettingsState
			{
				LaunchAtStartup = UserSettings.Current.LaunchAtStartup,
				LaunchAtStartupRegistered = registrationStatus.IsRegistered,
				LaunchAtStartupError = registrationStatus.Error,
				OnboardingCompleted = UserSettings.Current.OnboardingCompleted,
				Theme = UserSettings.Current.Theme
			}
		};
		LogRecordingControlSnapshot(coreBridgeSnapshot);
		return coreBridgeSnapshot;
	}

	private void LogRecordingControlSnapshot(CoreBridgeSnapshot snapshot)
	{
		string text = string.Join("|", snapshot.Auth.State, snapshot.DesktopSync.HasConfig, snapshot.DesktopSync.ConfigVersion, snapshot.Recording.State, snapshot.Recording.Transition ?? "-", snapshot.Recording.Expected, snapshot.Recording.ManualControlsAllowed, snapshot.Recording.HasActiveSession, snapshot.Recording.UnsupportedTopologyReason ?? "-", snapshot.Disk.LowDisk, snapshot.Update.Required);
		if (!string.Equals(text, _lastRecordingControlLogKey, StringComparison.Ordinal))
		{
			_lastRecordingControlLogKey = text;
			KuroLog.App($"CoreBridge: recording controls snapshot auth={snapshot.Auth.State} sync_has_config={snapshot.DesktopSync.HasConfig} config_version={snapshot.DesktopSync.ConfigVersion} state={snapshot.Recording.State} transition={snapshot.Recording.Transition ?? "-"} expected={snapshot.Recording.Expected} manual={snapshot.Recording.ManualControlsAllowed} active_session={snapshot.Recording.HasActiveSession} unsupported_topology={snapshot.Recording.UnsupportedTopologyReason ?? "-"} low_disk={snapshot.Disk.LowDisk} update_required={snapshot.Update.Required}", "LogRecordingControlSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 884);
		}
	}

	private UploadProgressInfo? LastUploadProgress()
	{
		lock (_uploadProgressLock)
		{
			return _lastUploadProgress;
		}
	}

	private void OnUploadProgressChanged(UploadProgressInfo progress)
	{
		lock (_uploadProgressLock)
		{
			_lastUploadProgress = progress;
		}
		ScheduleBroadcast();
	}

	private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
	{
		_communications.SynchronizeOwner();
		if (e.State == KuroWindows.Auth.AuthState.SignedIn)
		{
			Task.Run((Func<Task?>)ConsentRecorder.Instance.FlushAsync);
		}
		BroadcastSnapshot();
	}

	private void OnCommunicationsHint(JsonElement hint)
	{
		_communications.Observe(hint);
	}

	internal bool IsCurrentCommunications(JsonElement payload)
	{
		if (_disposed || !_auth.IsSignedIn || payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("owner_user_id", out var value) || value.ValueKind != JsonValueKind.String || !payload.TryGetProperty("session_generation", out var value2) || value2.ValueKind != JsonValueKind.Number || !value2.TryGetInt32(out var value3))
		{
			return false;
		}
		if (string.Equals(value.GetString(), _auth.Session?.UserId, StringComparison.OrdinalIgnoreCase))
		{
			return value3 == _communications.SessionGeneration;
		}
		return false;
	}

	private void OnCommunicationsChanged(JsonElement payload)
	{
		PublishCommunications("communications_changed", payload);
	}

	private void OnCommunicationsUnsupported(JsonElement payload)
	{
		PublishCommunications("communications_unsupported", payload);
	}

	private void PublishCommunications(string type, JsonElement payload)
	{
		string text = payload.GetProperty("owner_user_id").GetString();
		if (!_disposed && _auth.IsSignedIn && string.Equals(text, _auth.Session?.UserId, StringComparison.OrdinalIgnoreCase))
		{
			int @int = payload.GetProperty("session_generation").GetInt32();
			if (@int == _communications.SessionGeneration)
			{
				EventReady?.Invoke(new CoreBridgeEvent
				{
					Type = type,
					Communications = payload,
					OwnerUserId = text,
					SessionGeneration = @int
				});
			}
		}
	}

	private void OnSessionStateChanged(SessionState state)
	{
		ClearRecordingTransition();
		BroadcastSnapshot();
	}

	private void OnRecordingControlChanged()
	{
		ScheduleBroadcast();
	}

	private void OnCaptureContentionChanged(bool contended)
	{
		BroadcastSnapshot();
	}

	private void OnDesktopSyncChanged(object? sender, DesktopSyncSnapshot snapshot)
	{
		if (snapshot.Status == "synced")
		{
			Task.Run((Func<Task?>)ConsentRecorder.Instance.FlushAsync);
		}
		ScheduleBroadcast();
	}

	private void SetRecordingTransition(string transition)
	{
		Volatile.Write(ref _recordingTransition, transition);
		BroadcastSnapshot();
	}

	private void ClearRecordingTransition()
	{
		Volatile.Write(ref _recordingTransition, null);
	}

	private void ScheduleBroadcast()
	{
		if (_disposed)
		{
			return;
		}
		lock (_broadcastLock)
		{
			if (_disposed || _broadcastPending)
			{
				return;
			}
			if (_debounceTimer == null)
			{
				_debounceTimer = new Timer((object? _) =>
				{
					BroadcastSnapshot();
				}, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			}
			_debounceTimer.Change(TimeSpan.FromMilliseconds(250.0), Timeout.InfiniteTimeSpan);
			_broadcastPending = true;
		}
	}

	private void BroadcastSnapshot()
	{
		if (_disposed)
		{
			return;
		}
		lock (_broadcastLock)
		{
			_broadcastPending = false;
		}
		lock (_broadcastEmitLock)
		{
			CoreBridgeEvent coreBridgeEvent;
			try
			{
				coreBridgeEvent = new CoreBridgeEvent
				{
					Snapshot = CreateSnapshot()
				};
			}
			catch (Exception ex)
			{
				KuroLog.App("CoreBridge: snapshot failed: " + ex.Message, "BroadcastSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 1038);
				return;
			}
			try
			{
				EventReady?.Invoke(coreBridgeEvent);
			}
			catch (Exception ex2)
			{
				KuroLog.App("CoreBridge: event dispatch failed type=" + coreBridgeEvent.Type + ": " + ex2.Message, "BroadcastSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 1048);
			}
		}
	}

	private void OpenUrl(string url)
	{
		CoreBridgeEvent obj = new CoreBridgeEvent
		{
			Type = "open_auth_url",
			Url = url
		};
		if (EventReady != null)
		{
			try
			{
				EventReady(obj);
				return;
			}
			catch (Exception ex)
			{
				KuroLog.App("CoreBridge: auth URL event dispatch failed: " + ex.Message, "OpenUrl", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 1065);
			}
		}
		OpenExternalUrl(url);
	}

	private static void OpenExternalUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri result) || (result.Scheme != Uri.UriSchemeHttps && result.Scheme != Uri.UriSchemeHttp))
		{
			throw new CoreBridgeCommandException("invalid_url", "The link could not be opened.");
		}
		Process.Start(new ProcessStartInfo(result.ToString())
		{
			UseShellExecute = true
		});
	}

	public void BroadcastShowWindow()
	{
		if (_disposed)
		{
			return;
		}
		CoreBridgeEvent obj = new CoreBridgeEvent
		{
			Type = "show_window"
		};
		try
		{
			EventReady?.Invoke(obj);
		}
		catch (Exception ex)
		{
			KuroLog.App("CoreBridge: show window event dispatch failed: " + ex.Message, "BroadcastShowWindow", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 1093);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_auth.StateChanged -= OnAuthStateChanged;
			_session.StateChanged -= OnSessionStateChanged;
			_session.RecordingControlChanged -= OnRecordingControlChanged;
			_desktopSync.SnapshotChanged -= OnDesktopSyncChanged;
			_desktopSync.CommunicationsHintReceived -= OnCommunicationsHint;
			_communications.Changed -= OnCommunicationsChanged;
			_communications.Unsupported -= OnCommunicationsUnsupported;
			_communications.Dispose();
			_uploadManager.ProgressChanged -= OnUploadProgressChanged;
			_updates.Changed -= ScheduleBroadcast;
			_captureContention.Changed -= OnCaptureContentionChanged;
			_signInCancelledByUser = true;
			try
			{
				_cts.Cancel();
			}
			catch
			{
			}
			CancelSignIn();
			_cts.Dispose();
			lock (_broadcastLock)
			{
				_debounceTimer?.Dispose();
				_debounceTimer = null;
			}
			KuroLog.App("CoreBridge: stopped", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\CoreBridge\\CoreBridgeServer.cs", 1133);
		}
	}
}
