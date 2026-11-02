using System;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using DeviceIdentity;
using Microsoft.Win32;

namespace KuroWindows.Auth;

public sealed class AuthManager
{
	public static readonly AuthManager Instance = new AuthManager();

	private readonly APIClient _api = new APIClient();

	private readonly OAuthCallbackServer _callback = new OAuthCallbackServer();

	private readonly object _stateLock = new object();

	private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

	private readonly OncePerCodeGate _reregistrations = new OncePerCodeGate();

	private AuthState _state;

	private UserSessionSummary? _session;

	private string? _previousAccessToken;

	private string? _deviceId;

	private int _authOperationId;

	private string? _lastBanReason;

	private string? _lastLinkedEmail;

	private string? _lastSignInError;

	private string? _lastSignInErrorCode;

	private volatile bool _lastRestoreTransient;

	private static int _machineModelFaultLogged;

	public AuthState State => _state;

	public UserSessionSummary? Session => _session;

	public string? DeviceId => _deviceId;

	public bool IsSignedIn => _state == AuthState.SignedIn;

	public string? UserEmail => _session?.Email;

	public string? LastBanReason => _lastBanReason;

	public string? LastLinkedEmail => _lastLinkedEmail;

	public string? LastSignInError => _lastSignInError;

	public string? LastSignInErrorCode => _lastSignInErrorCode;

	public bool LastRestoreFailureWasTransient => _lastRestoreTransient;

	public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

	private AuthManager()
	{
	}

	public void StartCallbackServer(bool urlSchemeRegistered = true)
	{
		_callback.IsUrlSchemeRegistered = urlSchemeRegistered;
		_callback.Start();
	}

	private static bool IsTransientFailure(Exception ex)
	{
		if (ex is HttpRequestException { StatusCode: var statusCode })
		{
			if (!statusCode.HasValue)
			{
				return true;
			}
			if (statusCode.GetValueOrDefault() >= HttpStatusCode.InternalServerError)
			{
				return true;
			}
		}
		else
		{
			if (ex is TaskCanceledException)
			{
				return true;
			}
			if (ex is APIClientException { IsRetryable: var isRetryable })
			{
				return isRetryable;
			}
			if (ex is SocketException)
			{
				return true;
			}
			if (ex is DeviceCheckException { Kind: DeviceCheckErrorKind.ProofRejected })
			{
				return true;
			}
		}
		Exception innerException = ex.InnerException;
		return innerException != null && IsTransientFailure(innerException);
	}

	private async Task<UserSessionSummary> RefreshAsync(string refreshToken, CancellationToken ct = default(CancellationToken))
	{
		try
		{
			UserSessionSummary result = ToSummary(await _api.RefreshTokenAsync(refreshToken, ct).ConfigureAwait(continueOnCapturedContext: false));
			Count.AuthRefreshOk(1L);
			return result;
		}
		catch (Exception ex) when (!(ex is OperationCanceledException) || !ct.IsCancellationRequested)
		{
			AuthFailureReason authFailureReason = TelemetryMap.AuthFailureReasonFor(ex);
			Count.AuthRefreshFail(authFailureReason, 1L);
			Telemetry.Emit(Ev.AuthRefreshFailed(authFailureReason, TelemetryMap.HttpStatusOf(ex), IsTransientFailure(ex)));
			throw;
		}
	}

	public async Task RestoreSessionAsync()
	{
		_lastRestoreTransient = false;
		int operationId = BeginAuthOperation();
		Log($"op={operationId} restore: begin", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 107);
		SetState(AuthState.Restoring, null, null, null, operationId);
		try
		{
			string text = CredentialStore.Load(AppConfig.CredentialVaultResource);
			if (text == null)
			{
				Log($"op={operationId} restore: no stored credential", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 114);
				SetSignedOut(operationId);
				return;
			}
			UserSessionSummary userSessionSummary = JsonSerializer.Deserialize<UserSessionSummary>(text);
			if (userSessionSummary == null)
			{
				Log($"op={operationId} restore: failed to deserialize stored session", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 122);
				SetSignedOut(operationId);
				return;
			}
			Log($"op={operationId} restore: loaded session user_id={userSessionSummary.UserId} expires={userSessionSummary.ExpiresAt:O}", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 127);
			if (userSessionSummary.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5.0))
			{
				try
				{
					Log($"op={operationId} restore: stored session token refresh starting", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 134);
					userSessionSummary = await RefreshAsync(userSessionSummary.RefreshToken);
					Persist(userSessionSummary);
					Log($"op={operationId} restore: refresh succeeded, new expiry={userSessionSummary.ExpiresAt:O}", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 137);
				}
				catch (Exception ex)
				{
					_lastRestoreTransient = IsTransientFailure(ex);
					Log($"op={operationId} restore: refresh failed (transient={_lastRestoreTransient}): {ex}", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 142);
					SetSignedOut(operationId);
					return;
				}
			}
			if (!IsCurrentOperation(operationId))
			{
				Log($"op={operationId} restore: abandoned because a newer auth operation exists", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 150);
			}
			else
			{
				_api.SetAccessToken(userSessionSummary.AccessToken);
				Log($"op={operationId} restore: completing device-verified sign-in", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 155);
				await CompleteDeviceVerifiedSignInAsync(userSessionSummary, operationId).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (DeviceCheckException ex2) when (IsTerminalDeviceCheck(ex2.Kind))
		{
			SetLastSignInError(ex2, operationId);
			CredentialStore.Delete(AppConfig.CredentialVaultResource);
			_api.SetAccessToken("");
			Log($"op={operationId} restore: terminal device check failed: {ex2.Kind} {ex2.Message}", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 163);
		}
		catch (Exception ex3)
		{
			_lastRestoreTransient = IsTransientFailure(ex3);
			SetLastSignInError(ex3, operationId);
			Log($"op={operationId} restore: unhandled failure (transient={_lastRestoreTransient}): {ex3}", "RestoreSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 169);
			SetSignedOut(operationId);
		}
	}

	public async Task BeginSignInAsync(Action<string> openUrl, CancellationToken ct)
	{
		int operationId = BeginAuthOperation();
		Log($"op={operationId} sign-in: begin", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 184);
		SetState(AuthState.SigningIn, null, null, null, operationId);
		LoopbackOAuthCallbackServer loopback = null;
		try
		{
			string verifier = GenerateCodeVerifier();
			string value = ComputeCodeChallenge(verifier);
			string text = AppConfig.NativeAuthRedirectUrl;
			Task<string> task;
			try
			{
				loopback = new LoopbackOAuthCallbackServer(AppConfig.LoopbackAuthRedirectUrl);
				loopback.Start();
				text = loopback.RedirectUrl;
				task = loopback.WaitForCallbackAsync(ct);
				Log($"op={operationId} sign-in: using loopback redirect {text}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 201);
			}
			catch (Exception ex)
			{
				loopback?.Dispose();
				loopback = null;
				Log($"op={operationId} sign-in: loopback redirect unavailable, falling back to custom scheme: {ex.Message}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 207);
				Log($"op={operationId} sign-in: redirect fallback engaged (loopback->custom scheme)", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 210);
				if (!_callback.IsUrlSchemeRegistered)
				{
					Log($"op={operationId} sign-in: URL scheme not registered, aborting", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 214);
					throw new InvalidOperationException("Sign-in unavailable: the loopback callback and URL scheme handler are unavailable. Please restart the app or reinstall.");
				}
				task = _callback.WaitForCallbackAsync(ct);
				Log($"op={operationId} sign-in: using custom-scheme redirect {text}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 221);
			}
			string obj = $"{AppConfig.SupabaseUrl}/auth/v1/authorize?provider=google&redirect_to={Uri.EscapeDataString(text)}&code_challenge={value}&code_challenge_method=S256&prompt=select_account";
			Log($"op={operationId} sign-in: opening browser", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 235);
			openUrl(obj);
			string callbackUrl = await task;
			Log($"op={operationId} sign-in: callback received: {OAuthCallbackServer.DescribeForLog(callbackUrl)}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 239);
			if (!IsCurrentOperation(operationId))
			{
				Log($"op={operationId} sign-in: callback ignored because a newer auth operation exists", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 242);
				return;
			}
			string text2 = OAuthCallbackServer.ExtractCode(callbackUrl);
			if (text2 == null)
			{
				var (text3, text4) = ExtractOAuthError(callbackUrl);
				if (SignupPause.MatchesCallback(callbackUrl))
				{
					throw new SignupPausedException();
				}
				string text5 = SignupRegion.CallbackText(callbackUrl);
				if (text5 != null)
				{
					throw new RegionUnavailableException(text5);
				}
				Log($"op={operationId} sign-in: callback missing auth code; provider_error={text3 ?? "-"} provider_error_code={text4 ?? "-"}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 254);
				throw new Exception("No authorization code in callback URL.");
			}
			Log($"op={operationId} sign-in: extracted authorization code", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 257);
			UserSessionSummary userSessionSummary = ToSummary(await _api.ExchangePkceCodeAsync(text2, verifier));
			Log($"op={operationId} sign-in: token exchange succeeded user_id={userSessionSummary.UserId} expires={userSessionSummary.ExpiresAt:O}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 261);
			if (!IsCurrentOperation(operationId))
			{
				Log($"op={operationId} sign-in: token result ignored because a newer auth operation exists", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 265);
			}
			else
			{
				_api.SetAccessToken(userSessionSummary.AccessToken);
				Log($"op={operationId} sign-in: completing device-verified sign-in", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 270);
				await CompleteDeviceVerifiedSignInAsync(userSessionSummary, operationId).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException)
		{
			Log($"op={operationId} sign-in: cancelled", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 276);
			SetSignedOut(operationId);
			throw;
		}
		catch (DeviceCheckException ex3) when (IsTerminalDeviceCheck(ex3.Kind))
		{
			SetLastSignInError(ex3, operationId);
			_api.SetAccessToken("");
			Log($"op={operationId} sign-in: terminal device check failed: {ex3.Kind} {ex3.Message}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 284);
			throw;
		}
		catch (Exception ex4)
		{
			SetLastSignInError(ex4, operationId);
			Log($"op={operationId} sign-in: failed: {ex4}", "BeginSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 290);
			SetSignedOut(operationId);
			if (!(ex4 is SignupPausedException) && SignupPause.Matches(ex4))
			{
				throw new SignupPausedException(ex4);
			}
			if (!(ex4 is RegionUnavailableException))
			{
				string text6 = SignupRegion.Text(ex4);
				if (text6 != null)
				{
					throw new RegionUnavailableException(text6, ex4);
				}
			}
			throw;
		}
		finally
		{
			loopback?.Dispose();
		}
	}

	public Task SignOutAsync()
	{
		lock (_stateLock)
		{
			if (_state == AuthState.SignedOut && _session == null && _deviceId == null)
			{
				Log("sign-out: ignored because auth is already signed out", "SignOutAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 314);
				return Task.CompletedTask;
			}
		}
		int value = BeginAuthOperation();
		Log($"op={value} sign-out: begin", "SignOutAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 320);
		lock (_stateLock)
		{
			CredentialStore.Delete(AppConfig.CredentialVaultResource);
			_session = null;
			_previousAccessToken = null;
			_deviceId = null;
		}
		_lastBanReason = null;
		_lastLinkedEmail = null;
		_lastSignInError = null;
		_lastSignInErrorCode = null;
		_api.SetAccessToken("");
		SetState(AuthState.SignedOut, null, null, null, value);
		return Task.CompletedTask;
	}

	public Task<string> GetValidTokenAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return TokenAsync(null, cancellationToken);
	}

	internal async Task<string?> TokenAfterRejectionAsync(string rejectedAccessToken, CancellationToken cancellationToken)
	{
		UserSessionSummary session;
		string previousAccessToken;
		lock (_stateLock)
		{
			session = _session;
			previousAccessToken = _previousAccessToken;
		}
		if (session == null || (!string.Equals(rejectedAccessToken, session.AccessToken, StringComparison.Ordinal) && !string.Equals(rejectedAccessToken, previousAccessToken, StringComparison.Ordinal)))
		{
			return null;
		}
		return await TokenAsync(rejectedAccessToken, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static bool MustRefresh(UserSessionSummary session, string? rejectedAccessToken)
	{
		if (rejectedAccessToken == null)
		{
			return session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5.0);
		}
		return string.Equals(session.AccessToken, rejectedAccessToken, StringComparison.Ordinal);
	}

	private async Task<string> TokenAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
	{
		int opId = Volatile.Read(in _authOperationId);
		UserSessionSummary session = _session;
		if (session == null)
		{
			throw new InvalidOperationException("Not signed in.");
		}
		if (!MustRefresh(session, rejectedAccessToken))
		{
			return session.AccessToken;
		}
		APIClientException rejected = null;
		await _refreshLock.WaitAsync(cancellationToken);
		try
		{
			UserSessionSummary current = _session;
			if (current == null)
			{
				throw new InvalidOperationException("Signed out during token refresh.");
			}
			if (!MustRefresh(current, rejectedAccessToken))
			{
				return current.AccessToken;
			}
			UserSessionSummary userSessionSummary;
			try
			{
				userSessionSummary = await RefreshAsync(current.RefreshToken, cancellationToken);
			}
			catch (APIClientException ex) when (((Func<bool>)delegate
			{
				// Could not convert BlockContainer to single expression
				int statusCode = ex.StatusCode;
				return ((uint)(statusCode - 400) <= 1u || statusCode == 403) ? true : false;
			}).Invoke())
			{
				rejected = ex;
				throw;
			}
			lock (_stateLock)
			{
				if (!IsCurrentOperation(opId) || (object)_session != current)
				{
					throw new InvalidOperationException("Signed out during token refresh.");
				}
				_previousAccessToken = current.AccessToken;
				_session = userSessionSummary;
				_api.SetAccessToken(userSessionSummary.AccessToken);
				Persist(userSessionSummary);
			}
			Log($"token refresh ok, new expiry={userSessionSummary.ExpiresAt:O}", "TokenAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 440);
			return userSessionSummary.AccessToken;
		}
		finally
		{
			_refreshLock.Release();
			if (rejected != null && IsCurrentOperation(opId))
			{
				Log($"token refresh rejected status={rejected.StatusCode} code={rejected.Code ?? "-"}; signing out", "TokenAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 448);
				Telemetry.Emit(Ev.AuthForcedSignout(SignoutTrigger.RefreshRejected, rejected.StatusCode));
				SignOutAsync();
			}
		}
	}

	internal Task<bool> ReregisterDeviceAsync(string code, string operation, CancellationToken cancellationToken)
	{
		if (!IsSignedIn)
		{
			return Task.FromResult(result: false);
		}
		return _reregistrations.RunAsync(code, () => RunReregistrationAsync(code, operation), cancellationToken);
	}

	private async Task<bool> RunReregistrationAsync(string code, string operation)
	{
		int operationId = Volatile.Read(in _authOperationId);
		UserSessionSummary session = _session;
		string keyStatus = null;
		bool answered = false;
		string value;
		if (session == null)
		{
			value = "failed";
		}
		else
		{
			try
			{
				keyStatus = (await RegisterDeviceAsync(session, operationId).ConfigureAwait(continueOnCapturedContext: false)).KeyStatus;
				value = "registered";
				answered = true;
			}
			catch (DeviceCheckException ex)
			{
				(value, answered) = ex.Kind switch
				{
					DeviceCheckErrorKind.Banned => ("banned", true), 
					DeviceCheckErrorKind.AlreadyLinked => ("linked", true), 
					DeviceCheckErrorKind.UserBanned => ("user_banned", true), 
					DeviceCheckErrorKind.ProofRejected => ("proof_rejected", true), 
					_ => ("failed", false), 
				};
			}
			catch (Exception)
			{
				value = "failed";
			}
		}
		Log($"op={operationId} device-check: re-registration trigger={code} operation={operation} outcome={value} key_status={keyStatus ?? "-"} upload_signing={(DeviceKey.UploadProofEnabled ? "on" : "off")}", "RunReregistrationAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 521);
		return answered;
	}

	private async Task EnrolKeyWhenReadyAsync()
	{
		if (await DeviceKey.EnsureReadyAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(continueOnCapturedContext: false))
		{
			await ReregisterDeviceAsync("device_key_ready", "launch", CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task<DeviceCheckResult> RegisterDeviceAsync(UserSessionSummary session, int operationId)
	{
		try
		{
			string hwUuid = GetWmiHardwareUuid();
			string machineGuid = GetMachineGuid();
			if (hwUuid == null && machineGuid == null)
			{
				throw new DeviceCheckException(DeviceCheckErrorKind.NoHardwareUuid);
			}
			Task<bool> keyTask = DeviceKey.EnsureReadyAsync(AppConfig.DeviceKeyBudget);
			Task<DeviceChipClaim?> chipTask = TpmEndorsement.GetChipClaimAsync(AppConfig.EndorsementCertificateBudget);
			Task<JsonElement?> facetsTask = HardwareFacets.CollectAsync(AppConfig.HardwareFacetsBudget);
			await Task.WhenAll(keyTask, chipTask, facetsTask).ConfigureAwait(continueOnCapturedContext: false);
			DeviceKey.Snapshot snapshot = (keyTask.Result ? DeviceKey.Current : null);
			DeviceChipClaim result = chipTask.Result;
			JsonElement? result2 = facetsTask.Result;
			Log($"op={operationId} device-check: registering uuid={hwUuid ?? "(none)"} hasMachineGuid={machineGuid != null} user_id={session.UserId} key={snapshot?.AttestationKind ?? "none"} chip_source={result?.Source ?? "none"} facets_bytes={(result2.HasValue ? HardwareFacets.LastBytes : 0)}", "RegisterDeviceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 556);
			string machineModel = GetMachineModel();
			string osVersion = GetOsVersion();
			string hostname = GetHostname();
			DeviceCheckResult deviceCheckResult = await _api.RegisterDeviceAsync(hwUuid, "windows", machineModel, osVersion, hostname, machineGuid, session.UserId, snapshot?.PublicKeyBase64, snapshot?.AttestationKind, result, result2).ConfigureAwait(continueOnCapturedContext: false);
			Log($"op={operationId} device-check: status={deviceCheckResult.Status} key_status={deviceCheckResult.KeyStatus ?? "-"} signed={deviceCheckResult.Signed}", "RegisterDeviceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 564);
			DeviceKey.NoteRegistration(deviceCheckResult.Signed, deviceCheckResult.KeyStatus);
			string banReason = deviceCheckResult.BanReason;
			if (!string.IsNullOrWhiteSpace(deviceCheckResult.DeviceId))
			{
				lock (_stateLock)
				{
					_deviceId = deviceCheckResult.DeviceId;
				}
			}
			switch (deviceCheckResult.Status.ToLowerInvariant())
			{
			case "device_banned":
				SetState(AuthState.DeviceBanned, null, banReason, null, operationId);
				throw new DeviceCheckException(DeviceCheckErrorKind.Banned, banReason);
			case "linked_to_other_user":
				SetState(AuthState.DeviceLinkedToOtherUser, null, null, deviceCheckResult.LinkedEmail, operationId);
				throw new DeviceCheckException(DeviceCheckErrorKind.AlreadyLinked, deviceCheckResult.LinkedEmail);
			case "profile_banned":
				SetState(AuthState.UserBanned, null, null, null, operationId);
				throw new DeviceCheckException(DeviceCheckErrorKind.UserBanned);
			default:
				if (string.IsNullOrWhiteSpace(deviceCheckResult.DeviceId))
				{
					throw new InvalidOperationException("Device registration succeeded without returning a device_id.");
				}
				return deviceCheckResult;
			}
		}
		catch (DeviceCheckException)
		{
			throw;
		}
		catch (APIClientException ex2) when (IsProofRejection(ex2))
		{
			DeviceKey.NoteRegistration(ex2.ProofSigned, null);
			Log($"op={operationId} device-check: proof verdict stood status={ex2.StatusCode} code={ex2.Code ?? "-"} signed={ex2.ProofSigned}; key kept, registration retries later", "RegisterDeviceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 610);
			throw new DeviceCheckException(DeviceCheckErrorKind.ProofRejected, ex2.Code);
		}
		catch (HttpRequestException ex3)
		{
			Log($"op={operationId} device-check: network failure, refusing sign-in without registered device: {ex3.Message}", "RegisterDeviceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 615);
			throw;
		}
	}

	private static bool IsProofRejection(APIClientException ex)
	{
		int statusCode = ex.StatusCode;
		bool flag = ((statusCode == 400 || statusCode == 403) ? true : false);
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3;
			switch (ex.Code)
			{
			case "device_proof_invalid":
			case "device_key_mismatch":
			case "invalid_device_proof":
			case "device_proof_required":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			flag2 = flag3;
		}
		return flag2;
	}

	private async Task CompleteProfileSignInAsync(UserSessionSummary session, int operationId)
	{
		Log($"op={operationId} profile-check: calling desktop-v2-complete-signin user_id={session.UserId}", "CompleteProfileSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 630);
		CompleteSignInResponse completeSignInResponse = await _api.CompleteSignInAsync().ConfigureAwait(continueOnCapturedContext: false);
		switch (completeSignInResponse.Status.Trim().ToLowerInvariant())
		{
		case "profile_banned":
			SetState(AuthState.UserBanned, null, null, null, operationId);
			throw new DeviceCheckException(DeviceCheckErrorKind.UserBanned);
		case "signup_disabled":
			throw new SignupPausedException();
		default:
			throw new InvalidOperationException("Unexpected complete-signin status '" + completeSignInResponse.Status + "'.");
		}
	}

	private async Task CompleteDeviceVerifiedSignInAsync(UserSessionSummary session, int operationId)
	{
		if (!IsCurrentOperation(operationId))
		{
			Log($"op={operationId} complete-sign-in: ignored before device check because a newer auth operation exists", "CompleteDeviceVerifiedSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 657);
			return;
		}
		Log($"op={operationId} complete-sign-in: starting profile bootstrap", "CompleteDeviceVerifiedSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 661);
		await CompleteProfileSignInAsync(session, operationId).ConfigureAwait(continueOnCapturedContext: false);
		if (!IsCurrentOperation(operationId))
		{
			Log($"op={operationId} complete-sign-in: ignored after profile bootstrap because a newer auth operation exists", "CompleteDeviceVerifiedSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 666);
			return;
		}
		Log($"op={operationId} complete-sign-in: starting device registration", "CompleteDeviceVerifiedSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 670);
		DeviceCheckResult deviceCheckResult = await RegisterDeviceAsync(session, operationId).ConfigureAwait(continueOnCapturedContext: false);
		if (!IsCurrentOperation(operationId))
		{
			Log($"op={operationId} complete-sign-in: ignored after device check because a newer auth operation exists", "CompleteDeviceVerifiedSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 675);
			return;
		}
		lock (_stateLock)
		{
			_session = session;
			_previousAccessToken = null;
		}
		_lastBanReason = null;
		_lastLinkedEmail = null;
		_lastSignInError = null;
		_lastSignInErrorCode = null;
		Persist(session);
		Log($"op={operationId} complete-sign-in: session committed user_id={session.UserId}", "CompleteDeviceVerifiedSignInAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 690);
		SetState(AuthState.SignedIn, session, null, null, operationId);
		if (!deviceCheckResult.Signed && DeviceKey.RegistrationProofEnabled)
		{
			EnrolKeyWhenReadyAsync();
		}
	}

	private int BeginAuthOperation()
	{
		lock (_stateLock)
		{
			_lastSignInError = null;
			_lastSignInErrorCode = null;
			return Interlocked.Increment(ref _authOperationId);
		}
	}

	private void SetLastSignInError(Exception error, int operationId)
	{
		bool flag = IsUpdateRequired(error);
		lock (_stateLock)
		{
			if (!IsCurrentOperation(operationId))
			{
				return;
			}
			bool flag2 = !flag && SignupPause.Matches(error);
			string text = ((flag | flag2) ? null : SignupRegion.Text(error));
			ref string? lastSignInErrorCode = ref _lastSignInErrorCode;
			string? text2;
			if (flag2)
			{
				text2 = "signup_disabled";
			}
			else
			{
				text2 = ((text != null) ? "region_unavailable" : null);
			}
			lastSignInErrorCode = text2;
			ref string? lastSignInError = ref _lastSignInError;
			string? text3;
			if (flag)
			{
				text3 = null;
			}
			else if (flag2)
			{
				text3 = "Sign-ups are currently paused.";
			}
			else if (text != null)
			{
				text3 = text;
			}
			else
			{
				text3 = ((error is DeviceCheckException ex) ? DeviceCheckMessage(ex) : error.Message);
			}
			lastSignInError = text3;
		}
		if (flag)
		{
			Log($"op={operationId} update-required: the server refused this version (426); the update routine installs the update", "SetLastSignInError", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 729);
		}
	}

	private static bool IsUpdateRequired(Exception error)
	{
		if (!(error is APIClientException { IsUpdateRequired: not false }))
		{
			Exception innerException = error.InnerException;
			if (innerException != null)
			{
				return IsUpdateRequired(innerException);
			}
			return false;
		}
		return true;
	}

	private bool IsCurrentOperation(int operationId)
	{
		return operationId == Volatile.Read(in _authOperationId);
	}

	private void SetSignedOut(int operationId)
	{
		if (!IsCurrentOperation(operationId))
		{
			Log($"op={operationId} set-signed-out: ignored because a newer auth operation exists", "SetSignedOut", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 744);
			return;
		}
		lock (_stateLock)
		{
			_session = null;
			_previousAccessToken = null;
			_deviceId = null;
		}
		Log($"op={operationId} set-signed-out: session cleared", "SetSignedOut", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 755);
		SetState(AuthState.SignedOut, null, null, null, operationId);
	}

	private static string GenerateCodeVerifier()
	{
		byte[] array = new byte[32];
		RandomNumberGenerator.Fill(array);
		return Base64UrlEncode(array);
	}

	private static string ComputeCodeChallenge(string verifier)
	{
		return Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
	}

	private static string Base64UrlEncode(byte[] bytes)
	{
		return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-')
			.Replace('/', '_');
	}

	private static (string? Error, string? ErrorCode) ExtractOAuthError(string callbackUrl)
	{
		string item = null;
		string item2 = null;
		int num = callbackUrl.IndexOf('?');
		if (num < 0)
		{
			return (Error: null, ErrorCode: null);
		}
		string[] array = callbackUrl.Substring(num + 1).Split('&');
		foreach (string text in array)
		{
			int num2 = text.IndexOf('=');
			if (num2 >= 0)
			{
				string text2 = Uri.UnescapeDataString(text.Substring(0, num2));
				string text3 = Uri.UnescapeDataString(text.Substring(num2 + 1));
				if (text2 == "error")
				{
					item = text3;
				}
				else if (text2 == "error_code")
				{
					item2 = text3;
				}
			}
		}
		return (Error: item, ErrorCode: item2);
	}

	private static string? GetWmiHardwareUuid()
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				string text = item["UUID"]?.ToString();
				if (text != null && text != "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
				{
					return text;
				}
			}
		}
		catch
		{
		}
		Log("device-check: WMI hardware uuid unavailable; server will key on MachineGuid", "GetWmiHardwareUuid", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 828);
		return null;
	}

	private static string? GetMachineGuid()
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography");
			string? text = registryKey?.GetValue("MachineGuid")?.ToString();
			if (text == null)
			{
				Log("device-check: MachineGuid unavailable: value missing", "GetMachineGuid", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 839);
			}
			return text;
		}
		catch (Exception ex)
		{
			Log("device-check: MachineGuid unavailable: " + ex.Message, "GetMachineGuid", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 844);
			return null;
		}
	}

	internal static string GetMachineModel()
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
			using ManagementObjectCollection.ManagementObjectEnumerator managementObjectEnumerator = managementObjectSearcher.Get().GetEnumerator();
			if (managementObjectEnumerator.MoveNext())
			{
				return ((ManagementObject)managementObjectEnumerator.Current)["Model"]?.ToString() ?? "Unknown";
			}
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _machineModelFaultLogged, 1) == 0)
			{
				Log("device-check: machine model unavailable: " + KuroLog.ExceptionSummary(ex) + "; sending Unknown", "GetMachineModel", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 865);
			}
		}
		return "Unknown";
	}

	internal static string GetOsVersion()
	{
		return $"Windows {Environment.OSVersion.Version}";
	}

	private static string GetHostname()
	{
		return Environment.MachineName;
	}

	private void SetState(AuthState state, UserSessionSummary? session = null, string? banReason = null, string? linkedEmail = null, int? operationId = null)
	{
		if (operationId.HasValue && !IsCurrentOperation(operationId.Value))
		{
			Log($"op={operationId.Value} set-state: ignored transition to {state} because a newer auth operation exists", "SetState", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 883);
			return;
		}
		_state = state;
		_lastBanReason = banReason;
		_lastLinkedEmail = linkedEmail;
		Log($"op={operationId?.ToString() ?? "-"} state={state} user_id={session?.UserId ?? _session?.UserId ?? "-"} reason={banReason ?? "-"}", "SetState", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 890);
		EventHandler<AuthStateChangedEventArgs> eventHandler = StateChanged;
		if (eventHandler == null)
		{
			return;
		}
		AuthStateChangedEventArgs e = new AuthStateChangedEventArgs(state, session, banReason, linkedEmail);
		Delegate[] invocationList = eventHandler.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			EventHandler<AuthStateChangedEventArgs> eventHandler2 = (EventHandler<AuthStateChangedEventArgs>)invocationList[i];
			try
			{
				eventHandler2(this, e);
			}
			catch (Exception value)
			{
				Log($"op={operationId?.ToString() ?? "-"} state-handler-failed: {eventHandler2.Method.DeclaringType?.FullName}.{eventHandler2.Method.Name}: {value}", "SetState", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\AuthManager.cs", 906);
			}
		}
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Auth(message, memberName, filePath, lineNumber);
	}

	private static bool IsTerminalDeviceCheck(DeviceCheckErrorKind kind)
	{
		if ((uint)kind <= 2u)
		{
			return true;
		}
		return false;
	}

	private static string DeviceCheckMessage(DeviceCheckException ex)
	{
		return ex.Kind switch
		{
			DeviceCheckErrorKind.AlreadyLinked => string.IsNullOrWhiteSpace(ex.Message) ? "This PC is already linked to another Kuro account." : ("This PC is already linked to " + ex.Message + "."), 
			DeviceCheckErrorKind.Banned => string.IsNullOrWhiteSpace(ex.Message) ? "This device is suspended." : ex.Message, 
			DeviceCheckErrorKind.UserBanned => "This account is suspended.", 
			DeviceCheckErrorKind.ProofRejected => "Device verification did not complete. Please try again.", 
			_ => ex.Message, 
		};
	}

	private static void Persist(UserSessionSummary session)
	{
		CredentialStore.Save(AppConfig.CredentialVaultResource, AppConfig.CredentialVaultKey, JsonSerializer.Serialize(session));
	}

	private static UserSessionSummary ToSummary(SupabaseTokenResponse r)
	{
		return new UserSessionSummary
		{
			UserId = (r.User?.Id ?? ""),
			Email = (r.User?.Email ?? ""),
			AccessToken = r.AccessToken,
			RefreshToken = r.RefreshToken,
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(r.ExpiresIn)
		};
	}
}
