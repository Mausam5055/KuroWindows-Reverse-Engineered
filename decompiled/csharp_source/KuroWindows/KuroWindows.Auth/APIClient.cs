using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DesktopUpdates;
using DeviceIdentity;
using KuroWindows.Net;
using KuroWindows.Sync;
using KuroWindows.Update;
using KuroWindows.Upload;

namespace KuroWindows.Auth;

public sealed class APIClient
{
	private enum ProofCall
	{
		Registration,
		Upload
	}

	private readonly record struct Sent(HttpResponseMessage Response, bool Signed);

	private sealed class ProofContext
	{
		public DeviceKey.Snapshot Key { get; set; }

		public string UserId { get; }

		public byte[] SignedBody { get; }

		public string BodySha256Hex { get; }

		public string Operation { get; }

		internal ProofContext(DeviceKey.Snapshot key, string userId, byte[] signedBody, string operation)
		{
			Key = key;
			UserId = userId;
			SignedBody = signedBody;
			BodySha256Hex = DeviceProof.BodyDigestHex(signedBody);
			Operation = operation;
		}
	}

	private sealed record ProofVerdict(string Code, string? Reason, long? ServerTime)
	{
		public bool IsRefusal
		{
			get
			{
				switch (Code)
				{
				case "device_proof_invalid":
				case "device_key_mismatch":
				case "invalid_device_proof":
					return true;
				default:
					return false;
				}
			}
		}

		public bool IsDemand => Code == "device_proof_required";

		public bool IsClockRefusalWithServerTime
		{
			get
			{
				if (Code == "device_proof_invalid" && Reason == "clock")
				{
					return ServerTime.HasValue;
				}
				return false;
			}
		}
	}

	private static readonly HttpClient SharedHttp = new HttpClient(ResilientDns.CreateHandler());

	private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30.0);

	private static readonly TimeSpan ControlPlaneRequestTimeout = TimeSpan.FromSeconds(15.0);

	private static readonly JsonSerializerOptions _json = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static volatile bool _platformUp = true;

	private readonly HttpClient _http = SharedHttp;

	private string _accessToken = "";

	private readonly Func<string, CancellationToken, Task<string?>> _tokenAfterRejection = (string token, CancellationToken ct) => AuthManager.Instance.TokenAfterRejectionAsync(token, ct);

	private readonly Func<string?> _proofUserId = () => AuthManager.Instance.Session?.UserId;

	private readonly Func<DeviceKey.Snapshot?> _deviceKey = () => DeviceKey.Current;

	private readonly Func<string, string, CancellationToken, Task<bool>> _reregisterDevice = (string code, string operation, CancellationToken ct) => AuthManager.Instance.ReregisterDeviceAsync(code, operation, ct);

	private readonly Func<DeviceKey.Snapshot, Exception, DeviceKey.Snapshot> _reopenKey = (DeviceKey.Snapshot key, Exception cause) => DeviceKey.Reopen(key, cause);

	private static readonly TimeSpan SigningFailureLogInterval = TimeSpan.FromMinutes(10.0);

	private static long _signingFailureLoggedAt;

	private const string CheckVersionRoute = "desktop-v2-check-version";

	internal static JsonSerializerOptions JsonOptions => _json;

	public static bool PlatformUp => _platformUp;

	public APIClient()
	{
	}

	internal APIClient(HttpClient http, Func<string, CancellationToken, Task<string?>>? tokenAfterRejection = null, Func<string?>? proofUserId = null, Func<DeviceKey.Snapshot?>? deviceKey = null, Func<string, string, CancellationToken, Task<bool>>? reregisterDevice = null, Func<DeviceKey.Snapshot, Exception, DeviceKey.Snapshot>? reopenKey = null)
	{
		_http = http;
		if (tokenAfterRejection != null)
		{
			_tokenAfterRejection = tokenAfterRejection;
		}
		if (proofUserId != null)
		{
			_proofUserId = proofUserId;
		}
		if (deviceKey != null)
		{
			_deviceKey = deviceKey;
		}
		if (reregisterDevice != null)
		{
			_reregisterDevice = reregisterDevice;
		}
		if (reopenKey != null)
		{
			_reopenKey = reopenKey;
		}
	}

	public void SetAccessToken(string token)
	{
		_accessToken = token;
	}

	public async Task<SupabaseTokenResponse> ExchangePkceCodeAsync(string authCode, string codeVerifier)
	{
		string url = AppConfig.SupabaseUrl + "/auth/v1/token?grant_type=pkce";
		var body = new
		{
			auth_code = authCode,
			code_verifier = codeVerifier
		};
		using HttpResponseMessage resp = await PostAnonAsync(url, body, "pkce-token-exchange");
		if (!resp.IsSuccessStatusCode)
		{
			string responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
			throw new APIClientException("pkce-token-exchange", (int)resp.StatusCode, responseBody);
		}
		return (await resp.Content.ReadFromJsonAsync<SupabaseTokenResponse>(_json)) ?? throw new Exception("Empty token response");
	}

	public async Task<SupabaseTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default(CancellationToken))
	{
		string url = AppConfig.SupabaseUrl + "/auth/v1/token?grant_type=refresh_token";
		var body = new
		{
			refresh_token = refreshToken
		};
		using HttpResponseMessage resp = await PostAnonAsync(url, body, "refresh-token", cancellationToken);
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("refresh-token", resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return (await resp.Content.ReadFromJsonAsync<SupabaseTokenResponse>(_json)) ?? throw new Exception("Empty refresh response");
	}

	public async Task<DeviceCheckResult> RegisterDeviceAsync(string? hardwareUuid, string platform, string machineModel, string osVersion, string hostname, string? machineGuid, string userId, string? devicePublicKey = null, string? attestationKind = null, DeviceChipClaim? chip = null, JsonElement? hardwareFacets = null)
	{
		bool num = !string.IsNullOrEmpty(devicePublicKey) && !string.IsNullOrEmpty(attestationKind);
		byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(Body(withKey: false), _json);
		byte[] signedBytes = (num ? JsonSerializer.SerializeToUtf8Bytes(Body(withKey: true), _json) : null);
		Sent sent = await SendProofCallAsync(AppConfig.RegisterDeviceUrl, "desktop-v2-register-device", DefaultRequestTimeout, default, ProofCall.Registration, userId, unsignedBytes, signedBytes).ConfigureAwait(continueOnCapturedContext: false);
		using (HttpResponseMessage resp = sent.Response)
		{
			if (!resp.IsSuccessStatusCode)
			{
				APIClientException ex = await CreateApiExceptionAsync("desktop-v2-register-device", resp).ConfigureAwait(continueOnCapturedContext: false);
				ex.ProofSigned = sent.Signed;
				throw ex;
			}
			DeviceCheckResult deviceCheckResult = (await resp.Content.ReadFromJsonAsync<DeviceCheckResult>(_json).ConfigureAwait(continueOnCapturedContext: false)) ?? new DeviceCheckResult
			{
				Status = "ok"
			};
			return deviceCheckResult with
			{
				Signed = sent.Signed
			};
		}
		object Body(bool withKey)
		{
			return new
			{
				hardware_uuid = hardwareUuid,
				platform = platform,
				machine_model = machineModel,
				os_version = osVersion,
				hostname = hostname,
				app_version = AppConfig.AppVersion,
				machine_guid = machineGuid,
				device_public_key = (withKey ? devicePublicKey : null),
				attestation_kind = (withKey ? attestationKind : null),
				chip = (withKey ? chip : null),
				hardware_facets = hardwareFacets
			};
		}
	}

	public async Task<CompleteSignInResponse> CompleteSignInAsync()
	{
		using HttpResponseMessage resp = await SendAuthedAsync(HttpMethod.Post, AppConfig.CompleteSignInUrl, new { }, "desktop-v2-complete-signin", DefaultRequestTimeout).ConfigureAwait(continueOnCapturedContext: false);
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("desktop-v2-complete-signin", resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return (await resp.Content.ReadFromJsonAsync<CompleteSignInResponse>(_json).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new APIClientException("desktop-v2-complete-signin", (int)resp.StatusCode, null, "empty_response");
	}

	public async Task<bool> RecordConsentAsync(string docType, string docVersion, bool ageConfirmed, string? deviceId = null)
	{
		try
		{
			string url = AppConfig.SupabaseUrl + "/rest/v1/rpc/record_consent";
			var body = new
			{
				p_document_type = docType,
				p_document_version = docVersion,
				p_platform = "windows",
				p_device_id = deviceId,
				p_age_confirmed = ageConfirmed
			};
			using HttpResponseMessage httpResponseMessage = await SendAuthedAsync(HttpMethod.Post, url, body, "record-consent", ControlPlaneRequestTimeout);
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				KuroLog.Auth($"record-consent doc={docType} failed status={(int)httpResponseMessage.StatusCode}", "RecordConsentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 267);
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			KuroLog.Auth("record-consent doc=" + docType + " failed: " + ex.Message, "RecordConsentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 274);
			return false;
		}
	}

	public async Task CheckVersionAsync()
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, AppConfig.CheckVersionUrl)
		{
			Content = new StringContent("{}", Encoding.UTF8, "application/json")
		};
		httpRequestMessage.Headers.Add("apikey", AppConfig.SupabasePublishableKey);
		httpRequestMessage.Headers.Add("x-app-version", AppConfig.AppVersion);
		httpRequestMessage.Headers.Add("x-platform", "windows");
		httpRequestMessage.Headers.Add("x-app-name", "kuro");
		using HttpResponseMessage response = await SendAsync(httpRequestMessage, "check-version", ControlPlaneRequestTimeout).ConfigureAwait(continueOnCapturedContext: false);
		if (response.StatusCode == HttpStatusCode.UpgradeRequired)
		{
			string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
			AppUpdates.Heard("desktop-v2-check-version", responseBody);
			throw new APIClientException("check-version", 426, responseBody);
		}
		response.EnsureSuccessStatusCode();
	}

	public async Task<DesktopSyncResponse> DesktopSyncAsync(DesktopSyncRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		using HttpResponseMessage resp = await SendAuthedAsync(HttpMethod.Post, AppConfig.DesktopSyncUrl, request, "desktop-v2-sync", ControlPlaneRequestTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("desktop-v2-sync", resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return (await resp.Content.ReadFromJsonAsync<DesktopSyncResponse>(_json).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new Exception("Empty desktop-v2-sync response");
	}

	public async Task<JsonElement> PostDashboardActionAsync(string action, object actionParams, CancellationToken cancellationToken = default(CancellationToken), TimeSpan? requestTimeout = null)
	{
		DesktopDashboardRequest body = new DesktopDashboardRequest
		{
			Action = action,
			Params = actionParams
		};
		using HttpResponseMessage resp = await SendAuthedAsync(HttpMethod.Post, AppConfig.DesktopDashboardUrl, body, "desktop-dashboard:" + action, requestTimeout ?? ControlPlaneRequestTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("desktop-dashboard:" + action, resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return ((await resp.Content.ReadFromJsonAsync<DesktopDashboardEnvelope>(_json, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new Exception("Empty desktop-dashboard:" + action + " response")).Data;
	}

	public async Task<UploadRequestResponse> RequestUploadAsync(string clientSessionId, int segmentIndex, long claimedDurationMs, long idleDurationMs, DateTimeOffset capturedAt, IReadOnlyList<UploadFileDescriptor> files, string? deviceId = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		Dictionary<string, string> file_hashes = (files.All((UploadFileDescriptor f) => !string.IsNullOrWhiteSpace(f.Sha256)) ? files.ToDictionary((UploadFileDescriptor f) => f.Name, (UploadFileDescriptor f) => f.Sha256) : null);
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(new
		{
			client_session_id = clientSessionId,
			platform = "windows",
			claimed_duration_ms = claimedDurationMs,
			idle_duration_ms = idleDurationMs,
			captured_at = capturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
			segment_index = segmentIndex,
			device_id = (string.IsNullOrWhiteSpace(deviceId) ? null : deviceId),
			files = files,
			file_hashes = file_hashes,
			supports_multipart = true
		}, _json);
		using HttpResponseMessage resp = (await SendProofCallAsync(AppConfig.UploadRequestUrl, "desktop-v2-upload-request", DefaultRequestTimeout, cancellationToken, ProofCall.Upload, _proofUserId(), array, array).ConfigureAwait(continueOnCapturedContext: false)).Response;
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("desktop-v2-upload-request", resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return (await resp.Content.ReadFromJsonAsync<UploadRequestResponse>(_json, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new Exception("Empty desktop-v2-upload-request response");
	}

	public async Task<UploadCompleteResponse> CompleteUploadAsync(string clientSessionId, int segmentIndex, long totalUploadedBytes, IReadOnlyList<string> fileNames, string? deviceId, long claimedDurationMs = 0L, CancellationToken cancellationToken = default(CancellationToken))
	{
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(new
		{
			client_session_id = clientSessionId,
			platform = "windows",
			total_uploaded_bytes = totalUploadedBytes,
			segment_index = segmentIndex,
			files = fileNames,
			device_id = (string.IsNullOrWhiteSpace(deviceId) ? null : deviceId),
			claimed_duration_ms = claimedDurationMs
		}, _json);
		using HttpResponseMessage resp = (await SendProofCallAsync(AppConfig.UploadCompleteUrl, "desktop-v2-upload-complete", DefaultRequestTimeout, cancellationToken, ProofCall.Upload, _proofUserId(), array, array, UploadCompletionRegion.HeaderValue).ConfigureAwait(continueOnCapturedContext: false)).Response;
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("desktop-v2-upload-complete", resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return (await resp.Content.ReadFromJsonAsync<UploadCompleteResponse>(_json, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new Exception("Empty desktop-v2-upload-complete response");
	}

	public async Task<MultipartCompleteResponse> CompleteMultipartUploadAsync(MultipartCompleteRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(request, _json);
		using HttpResponseMessage resp = (await SendProofCallAsync(AppConfig.UploadMultipartCompleteUrl, "desktop-v2-upload-multipart-complete", DefaultRequestTimeout, cancellationToken, ProofCall.Upload, _proofUserId(), array, array, UploadCompletionRegion.HeaderValue).ConfigureAwait(continueOnCapturedContext: false)).Response;
		if (!resp.IsSuccessStatusCode)
		{
			throw await CreateApiExceptionAsync("desktop-v2-upload-multipart-complete", resp).ConfigureAwait(continueOnCapturedContext: false);
		}
		return (await resp.Content.ReadFromJsonAsync<MultipartCompleteResponse>(_json, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new Exception("Empty desktop-v2-upload-multipart-complete response");
	}

	private async Task<HttpResponseMessage> PostAnonAsync(string url, object body, string operation, CancellationToken cancellationToken = default(CancellationToken))
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = JsonContent(body)
		};
		httpRequestMessage.Headers.Add("apikey", AppConfig.SupabasePublishableKey);
		return await SendAsync(httpRequestMessage, operation, DefaultRequestTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
	{
		using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout);
		bool answered = false;
		try
		{
			HttpResponseMessage httpResponseMessage = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			answered = httpResponseMessage.StatusCode < HttpStatusCode.InternalServerError;
			return httpResponseMessage;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
		{
			int num = (int)Math.Ceiling(timeout.TotalSeconds);
			throw new APIClientException(operation, 408, $"request timed out after {num}s", "timeout", $"Request timed out after {num}s", TimeSpan.FromSeconds(Math.Min(num, 60)));
		}
		finally
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				_platformUp = answered;
			}
		}
	}

	private async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string url, object? body, string operation, TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
	{
		byte[] unsignedBytes = ((body == null) ? null : JsonSerializer.SerializeToUtf8Bytes(body, _json));
		return (await SendWithBearerRetryAsync(method, url, unsignedBytes, operation, timeout, cancellationToken, null, null).ConfigureAwait(continueOnCapturedContext: false)).Response;
	}

	private async Task<Sent> SendProofCallAsync(string url, string operation, TimeSpan timeout, CancellationToken cancellationToken, ProofCall call, string? userId, byte[] unsignedBytes, byte[]? signedBytes, string? region = null)
	{
		DeviceKey.Snapshot key = _deviceKey();
		bool canSign = signedBytes != null && key != null && !string.IsNullOrWhiteSpace(userId);
		bool flag = ((call == ProofCall.Registration) ? DeviceKey.RegistrationProofEnabled : DeviceKey.UploadProofEnabled);
		ProofContext proof = ((canSign & flag) ? new ProofContext(key, userId, signedBytes, operation) : null);
		Sent sent = await SendWithBearerRetryAsync(HttpMethod.Post, url, unsignedBytes, operation, timeout, cancellationToken, region, proof).ConfigureAwait(continueOnCapturedContext: false);
		int statusCode = (int)sent.Response.StatusCode;
		if ((statusCode != 400 && statusCode != 403) || 1 == 0)
		{
			return sent;
		}
		ProofVerdict verdict = await ReadProofVerdictAsync(sent.Response).ConfigureAwait(continueOnCapturedContext: false);
		if (verdict == null)
		{
			return sent;
		}
		if (sent.Signed && verdict.IsClockRefusalWithServerTime)
		{
			long value = DeviceProof.LearnClockOffset(verdict.ServerTime.Value);
			KuroLog.Auth($"device-proof: clock offset={value} applied", "SendProofCallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 597);
			sent.Response.Dispose();
			sent = await SendWithBearerRetryAsync(HttpMethod.Post, url, unsignedBytes, operation, timeout, cancellationToken, region, proof).ConfigureAwait(continueOnCapturedContext: false);
			statusCode = (int)sent.Response.StatusCode;
			if ((statusCode != 400 && statusCode != 403) || 1 == 0)
			{
				return sent;
			}
			verdict = await ReadProofVerdictAsync(sent.Response).ConfigureAwait(continueOnCapturedContext: false);
			if (verdict == null)
			{
				return sent;
			}
		}
		if (sent.Signed && verdict.IsRefusal)
		{
			if (call == ProofCall.Upload && verdict.Code == "device_key_mismatch")
			{
				Sent? sent2 = await ResendAfterReregistrationAsync(sent, verdict.Code, url, operation, timeout, cancellationToken, userId, unsignedBytes, signedBytes, region, key).ConfigureAwait(continueOnCapturedContext: false);
				if (sent2.HasValue)
				{
					return sent2.GetValueOrDefault();
				}
			}
			if ((call == ProofCall.Registration) ? DeviceKey.DisableRegistrationProof() : DeviceKey.DisableUploadProof())
			{
				KuroLog.Auth($"{operation} refused the device proof status={(int)sent.Response.StatusCode} code={verdict.Code} reason={verdict.Reason ?? "-"}; {Kind(call)} signing off for this process, re-sending once unsigned", "SendProofCallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 632);
			}
			sent.Response.Dispose();
			return await SendWithBearerRetryAsync(HttpMethod.Post, url, unsignedBytes, operation, timeout, cancellationToken, region, null).ConfigureAwait(continueOnCapturedContext: false);
		}
		if ((!sent.Signed && verdict.IsDemand) & canSign)
		{
			bool flipped;
			DeviceKey.Snapshot signer;
			bool flag2;
			if (call == ProofCall.Registration)
			{
				flipped = DeviceKey.EnableRegistrationProof();
				signer = key;
			}
			else
			{
				signer = (DeviceKey.ReenableUploadProof(out flipped) ? key : null);
				flag2 = signer == null;
				if (flag2)
				{
					flag2 = await _reregisterDevice(verdict.Code, operation, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (flag2)
				{
					signer = (DeviceKey.ReenableUploadProof(out flipped) ? key : null);
				}
			}
			if (signer == null)
			{
				return sent;
			}
			if (flipped)
			{
				KuroLog.Auth($"{operation} answered code={verdict.Code}; {Kind(call)} signing on, re-sending once signed", "SendProofCallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 674);
			}
			sent.Response.Dispose();
			ProofContext proof2 = new ProofContext(signer, userId, signedBytes, operation);
			sent = await SendWithBearerRetryAsync(HttpMethod.Post, url, unsignedBytes, operation, timeout, cancellationToken, region, proof2).ConfigureAwait(continueOnCapturedContext: false);
			flag2 = sent.Signed;
			if (flag2)
			{
				statusCode = (int)sent.Response.StatusCode;
				bool flag3 = ((statusCode == 400 || statusCode == 403) ? true : false);
				flag2 = flag3;
			}
			if (flag2)
			{
				ProofVerdict proofVerdict = await ReadProofVerdictAsync(sent.Response).ConfigureAwait(continueOnCapturedContext: false);
				if ((object)proofVerdict != null && proofVerdict.IsClockRefusalWithServerTime)
				{
					long value2 = DeviceProof.LearnClockOffset(proofVerdict.ServerTime.Value);
					KuroLog.Auth($"device-proof: clock offset={value2} applied", "SendProofCallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 689);
				}
				else if (call == ProofCall.Upload && proofVerdict?.Code == "device_key_mismatch")
				{
					Sent? sent3 = await ResendAfterReregistrationAsync(sent, proofVerdict.Code, url, operation, timeout, cancellationToken, userId, unsignedBytes, signedBytes, region, key).ConfigureAwait(continueOnCapturedContext: false);
					if (sent3.HasValue)
					{
						return sent3.GetValueOrDefault();
					}
				}
			}
			return sent;
		}
		return sent;
	}

	private async Task<Sent?> ResendAfterReregistrationAsync(Sent refused, string code, string url, string operation, TimeSpan timeout, CancellationToken cancellationToken, string? userId, byte[] unsignedBytes, byte[]? signedBytes, string? region, DeviceKey.Snapshot? key)
	{
		if (!(await _reregisterDevice(code, operation, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
		{
			return null;
		}
		refused.Response.Dispose();
		ProofContext proof = ((DeviceKey.UploadProofEnabled && key != null && signedBytes != null && !string.IsNullOrWhiteSpace(userId)) ? new ProofContext(key, userId, signedBytes, operation) : null);
		return await SendWithBearerRetryAsync(HttpMethod.Post, url, unsignedBytes, operation, timeout, cancellationToken, region, proof).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static string Kind(ProofCall call)
	{
		if (call != ProofCall.Registration)
		{
			return "upload";
		}
		return "registration";
	}

	private async Task<Sent> SendWithBearerRetryAsync(HttpMethod method, string url, byte[]? unsignedBytes, string operation, TimeSpan timeout, CancellationToken cancellationToken, string? region, ProofContext? proof)
	{
		string bearer = _accessToken;
		bool signed;
		using HttpRequestMessage req = BuildAuthedRequest(method, url, unsignedBytes, bearer, region, proof, out signed);
		HttpResponseMessage resp = await SendAsync(req, operation, timeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (resp.StatusCode != HttpStatusCode.Unauthorized)
		{
			return new Sent(resp, signed);
		}
		string text = await _tokenAfterRejection(bearer, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (text == null)
		{
			return new Sent(resp, signed);
		}
		resp.Dispose();
		KuroLog.Auth(operation + " answered 401 with the session token; retrying once after refresh", "SendWithBearerRetryAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 778);
		_accessToken = text;
		using HttpRequestMessage retry = BuildAuthedRequest(method, url, unsignedBytes, text, region, proof, out signed);
		return new Sent(await SendAsync(retry, operation, timeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false), signed);
	}

	private static async Task<ProofVerdict?> ReadProofVerdictAsync(HttpResponseMessage resp)
	{
		string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("error", out var value) || value.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			string text = StringProperty(value, "code");
			bool flag;
			switch (text)
			{
			case "device_proof_invalid":
			case "device_key_mismatch":
			case "invalid_device_proof":
			case "device_proof_required":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (!flag)
			{
				return null;
			}
			string text2 = StringProperty(value, "reason");
			long? num = Int64Property(value, "server_time");
			if (value.TryGetProperty("details", out var value2) && value2.ValueKind == JsonValueKind.Object)
			{
				if (text2 == null)
				{
					text2 = StringProperty(value2, "reason");
				}
				long? num2 = num;
				if (!num2.HasValue)
				{
					num = Int64Property(value2, "server_time");
				}
			}
			return new ProofVerdict(text, text2, num);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? StringProperty(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	private static long? Int64Property(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var value2))
		{
			return null;
		}
		return value2;
	}

	private HttpRequestMessage BuildAuthedRequest(HttpMethod method, string url, byte[]? unsignedBytes, string bearer, string? region, ProofContext? proof, out bool signed)
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(method, url)
		{
			Version = HttpVersion.Version20,
			VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
		};
		httpRequestMessage.Headers.Add("apikey", AppConfig.SupabasePublishableKey);
		httpRequestMessage.Headers.Add("x-app-version", AppConfig.AppVersion);
		httpRequestMessage.Headers.Add("x-platform", "windows");
		httpRequestMessage.Headers.Add("x-app-name", "kuro");
		if (region != null)
		{
			httpRequestMessage.Headers.Add("x-region", region);
		}
		if (!string.IsNullOrEmpty(bearer))
		{
			httpRequestMessage.Headers.Add("Authorization", "Bearer " + bearer);
		}
		byte[] array = unsignedBytes;
		signed = false;
		if (proof != null && TrySignProof(proof, out var headers))
		{
			httpRequestMessage.Headers.Add("x-device-key-id", headers.KeyId);
			httpRequestMessage.Headers.Add("x-device-ts", headers.Timestamp);
			httpRequestMessage.Headers.Add("x-device-signature", headers.Signature);
			array = proof.SignedBody;
			signed = true;
		}
		if (array != null)
		{
			httpRequestMessage.Content = JsonContent(array);
		}
		return httpRequestMessage;
	}

	private bool TrySignProof(ProofContext proof, out DeviceProof.Headers headers)
	{
		try
		{
			headers = Sign(proof, proof.Key);
			return true;
		}
		catch (Exception ex)
		{
			DeviceKey.Snapshot snapshot = null;
			string value;
			try
			{
				snapshot = _reopenKey(proof.Key, ex);
				headers = Sign(proof, snapshot);
				proof.Key = snapshot;
				return true;
			}
			catch (Exception ex2)
			{
				value = ((snapshot == null) ? "reopen" : "re-sign after reopen") + " failed: " + KuroLog.ExceptionSummary(ex2);
			}
			headers = default;
			long timestamp = Stopwatch.GetTimestamp();
			long num = Interlocked.Read(in _signingFailureLoggedAt);
			if ((num == 0L || Stopwatch.GetElapsedTime(num, timestamp) >= SigningFailureLogInterval) && Interlocked.CompareExchange(ref _signingFailureLoggedAt, timestamp, num) == num)
			{
				KuroLog.Auth($"device proof signing failed operation={proof.Operation} rung={DeviceKey.Name(proof.Key.Rung)}: {KuroLog.ExceptionSummary(ex)}; {value}; sending unsigned", "TrySignProof", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\APIClient.cs", 936);
			}
			return false;
		}
	}

	private static DeviceProof.Headers Sign(ProofContext proof, DeviceKey.Snapshot key)
	{
		return DeviceProof.BuildForDigest(key, proof.UserId, DeviceProof.NowUnixSeconds(), proof.BodySha256Hex);
	}

	private static ByteArrayContent JsonContent(byte[] body)
	{
		ByteArrayContent byteArrayContent = new ByteArrayContent(body);
		byteArrayContent.Headers.ContentType = new MediaTypeHeaderValue("application/json")
		{
			CharSet = "utf-8"
		};
		return byteArrayContent;
	}

	private static ByteArrayContent JsonContent(object body)
	{
		return JsonContent(JsonSerializer.SerializeToUtf8Bytes(body, _json));
	}

	private static async Task<APIClientException> CreateApiExceptionAsync(string operation, HttpResponseMessage resp)
	{
		string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
		string code = null;
		string serverMessage = null;
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			if (jsonDocument.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object)
			{
				if (value.TryGetProperty("code", out var value2))
				{
					code = value2.GetString();
				}
				if (value.TryGetProperty("message", out var value3))
				{
					serverMessage = value3.GetString();
				}
			}
			else
			{
				if (jsonDocument.RootElement.TryGetProperty("code", out var value4))
				{
					code = value4.GetString();
				}
				if (jsonDocument.RootElement.TryGetProperty("message", out var value5))
				{
					serverMessage = value5.GetString();
				}
			}
		}
		catch
		{
		}
		TimeSpan? timeSpan = resp.Headers.RetryAfter?.Delta;
		TimeSpan? timeSpan2;
		if (!timeSpan.HasValue)
		{
			DateTimeOffset? dateTimeOffset = resp.Headers.RetryAfter?.Date;
			if (dateTimeOffset.HasValue)
			{
				DateTimeOffset valueOrDefault = dateTimeOffset.GetValueOrDefault();
				timeSpan2 = valueOrDefault - DateTimeOffset.UtcNow;
			}
			else
			{
				timeSpan2 = null;
			}
		}
		else
		{
			timeSpan2 = timeSpan;
		}
		TimeSpan? retryAfter = timeSpan2;
		APIClientException ex = new APIClientException(operation, (int)resp.StatusCode, text, code, serverMessage, retryAfter);
		if (ex.IsUpdateRequired)
		{
			AppUpdates.Heard(operation, text);
		}
		return ex;
	}

	internal static APIClientException UpdateRequiredByStatus(string operation, string status)
	{
		AppUpdates.Heard(operation, null);
		return new APIClientException(operation, 426, status);
	}

	public static async Task<(UpdateCheckStatus Status, string? ErrorMessage)> CheckVersionResultAsync()
	{
		try
		{
			await new APIClient().CheckVersionAsync().ConfigureAwait(continueOnCapturedContext: false);
			return (Status: UpdateCheckStatus.UpToDate, ErrorMessage: null);
		}
		catch (APIClientException ex) when (ex.IsUpdateRequired)
		{
			return (Status: UpdateCheckStatus.UpdateRequired, ErrorMessage: null);
		}
		catch (Exception ex2)
		{
			return (Status: UpdateCheckStatus.Error, ErrorMessage: ex2.Message);
		}
	}
}
