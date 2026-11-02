using System;
using System.IO;
using System.Reflection;
using SystemAudio;
using DeviceIdentity;

namespace KuroWindows;

public static class AppConfig
{
	public const string AppName = "kuro";

	public static readonly Uri TelemetryIngestUrl = new Uri("https://rj4mawk0tj.execute-api.us-east-1.amazonaws.com/v1/batches");

	public const int DefaultCaptureFramesPerSecond = 30;

	public const string DeviceKeyName = "Kuro Device Key v1";

	public const string DeviceKeyRsaName = "Kuro Device Key v1 RSA";

	public const string PlatformCryptoProviderName = "Microsoft Platform Crypto Provider";

	public const string DeviceKeyIdHeader = "x-device-key-id";

	public const string DeviceTimestampHeader = "x-device-ts";

	public const string DeviceSignatureHeader = "x-device-signature";

	public const string DeviceProofPrefix = "kuro-device-proof/v1";

	public const string DeviceProofInvalidCode = "device_proof_invalid";

	public const string DeviceKeyMismatchCode = "device_key_mismatch";

	public const string InvalidDeviceProofCode = "invalid_device_proof";

	public const string DeviceProofRequiredCode = "device_proof_required";

	public const string DeviceProofClockReason = "clock";

	public const int HardwareFacetsMaxBytes = 3072;

	public const int EndorsementCertificateMaxBase64Chars = 4096;

	public const string EndorsementCertificateCacheFileName = "ek-cert.der";

	public const string EndorsementCertificateAttemptMarkerFileName = "ek-cert.attempted";

	public const string IntelEndorsementCertificateServiceUrl = "https://ekop.intel.com/ekcertservice/";

	public const string AmdEndorsementCertificateServiceUrl = "https://ftpm.amd.com/pki/aia/";

	public static string LocalAppName => "Kuro";

	public static string LocalDataFolderName => "Kuro";

	public static string MutexName => "Global\\KuroWindows_SingleInstance";

	public static string AppCommandPipeName => "KuroWindowsAppCommand";

	public static string OAuthCallbackPipeName => "KuroWindowsOAuthCallback";

	public static string UiaProbePipeNamePrefix => "KuroWindowsUiaProbe-";

	public static string StartupRunValueName => "KuroWindows";

	public static string SupabaseUrl => Environment.GetEnvironmentVariable("KURO_SUPABASE_URL") ?? "https://auth.trykuro.app";

	public static string SupabasePublishableKey => Environment.GetEnvironmentVariable("KURO_SUPABASE_PUBLISHABLE_KEY") ?? "sb_publishable_2rMMms2At3Zdbmx589hloQ_wjnd4tkZ";

	public static string CheckVersionUrl => SupabaseUrl + "/functions/v1/desktop-v2-check-version";

	public static string UpdateFeedUrl => Environment.GetEnvironmentVariable("KURO_UPDATE_FEED_URL") ?? "https://updates.trykuro.app/win";

	public static string DesktopDashboardUrl => SupabaseUrl + "/functions/v1/desktop-dashboard";

	public static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

	public static string AuthCallbackScheme => "com.kuro.app";

	public static bool EnableCustomSchemeFallback => !string.Equals(Environment.GetEnvironmentVariable("KURO_ENABLE_CUSTOM_SCHEME_FALLBACK"), "0", StringComparison.OrdinalIgnoreCase);

	public static string NativeAuthRedirectUrl => Environment.GetEnvironmentVariable("KURO_NATIVE_AUTH_REDIRECT_URL") ?? "https://trykuro.app/auth/native-callback";

	public static string LoopbackAuthRedirectUrl => Environment.GetEnvironmentVariable("KURO_LOOPBACK_AUTH_REDIRECT_URL") ?? "http://127.0.0.1:57391/auth/callback";

	public static string UploadRequestUrl => SupabaseUrl + "/functions/v1/desktop-v2-upload-request";

	public static string UploadCompleteUrl => SupabaseUrl + "/functions/v1/desktop-v2-upload-complete";

	public static string UploadMultipartCompleteUrl => SupabaseUrl + "/functions/v1/desktop-v2-upload-multipart-complete";

	public static string DesktopSyncUrl => SupabaseUrl + "/functions/v1/desktop-v2-sync";

	public static string CompleteSignInUrl => SupabaseUrl + "/functions/v1/desktop-v2-complete-signin";

	public static string RegisterDeviceUrl => SupabaseUrl + "/functions/v1/desktop-v2-register-device";

	public static string CredentialVaultResource => "com.kuro.app.auth";

	public static string CredentialVaultKey => "kuro.auth.session";

	public static int ClipDurationSeconds => 300;

	public static long MinimumDiskSpaceBytes => 2000000000L;

	public static int DiskCheckIntervalSeconds => 30;

	public static int CapabilityPrivacyPollIntervalMs
	{
		get
		{
			if (!int.TryParse(Environment.GetEnvironmentVariable("KURO_CAPABILITY_PRIVACY_POLL_MS"), out var result))
			{
				return 150;
			}
			return Math.Max(100, result);
		}
	}

	public static int CapabilityPollIntervalMs
	{
		get
		{
			if (!int.TryParse(Environment.GetEnvironmentVariable("KURO_CAPABILITY_POLL_MS"), out var result))
			{
				return 500;
			}
			return Math.Max(250, result);
		}
	}

	public static int S3UploadTimeoutMinutes => 30;

	public static bool CaptureDebugLoggingEnabled => string.Equals(Environment.GetEnvironmentVariable("KURO_CAPTURE_DEBUG"), "1", StringComparison.Ordinal);

	public static int[] BackgroundUploadRetryDelaysSeconds => new int[3] { 60, 180, 300 };

	public static int[] CaptureRetryDelaysSeconds => new int[6] { 3, 3, 3, 10, 30, 60 };

	public static int CaptureRetrySettleSeconds => 60;

	public static int MouseThrottleMs => 1;

	public static int WindowTitlePollMs => 500;

	public static int TextSelectionPollMs => 2000;

	public static int UiaProbeBridgeDeadlineMs => 400;

	public static int UiaProbeForegroundDeadlineMs => 750;

	public static int UiaProbePollDeadlineMs => 1500;

	public static int UiaProbeConnectTimeoutMs => 10000;

	public static int[] UiaProbeRespawnBackoffSeconds => new int[3] { 1, 5, 30 };

	public static int UiaProbeSpawnFailureLimit => 3;

	public static int UiaProbeAppDeathThreshold => 3;

	public static int UiaProbeAppDeathWindowMinutes => 10;

	public static int UiaProbeAppSuppressionMinutes => 30;

	public static int MouseDeltaFlushMs
	{
		get
		{
			if (!int.TryParse(Environment.GetEnvironmentVariable("KURO_MOUSE_DELTA_FLUSH_MS"), out var result))
			{
				return 8;
			}
			return Math.Max(0, result);
		}
	}

	public static string[] DeviceKeyAcknowledgedStatuses => new string[3] { "enrolled", "matched", "rotated" };

	public static TimeSpan DeviceKeyBudget => TimeSpan.FromSeconds(3.0);

	public static TimeSpan EndorsementCertificateBudget => TimeSpan.FromSeconds(3.0);

	public static TimeSpan HardwareFacetsBudget => TimeSpan.FromMilliseconds(2000.0);

	public static TimeSpan CloudMetadataProbeTimeout => TimeSpan.FromMilliseconds(200.0);

	public static string DeviceAuthDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LocalDataFolderName, "auth");

	public static DeviceIdentitySettings DeviceIdentity => new DeviceIdentitySettings("Kuro Device Key v1", "Kuro Device Key v1 RSA", "Microsoft Platform Crypto Provider", "kuro-device-proof/v1", DeviceKeyAcknowledgedStatuses, EndorsementCertificateBudget, HardwareFacetsBudget, CloudMetadataProbeTimeout, 3072, 4096, DeviceAuthDirectory, "ek-cert.der", "ek-cert.attempted", "https://ekop.intel.com/ekcertservice/", "https://ftpm.amd.com/pki/aia/");

	public static SystemAudioSettings SystemAudio => new SystemAudioSettings(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LocalDataFolderName), "system_audio_probe.json", AppVersion, Environment.OSVersion.Version.ToString(), Environment.GetEnvironmentVariable("KURO_SYSTEM_AUDIO"), "Kuro");

	public static long MinimumDiskSpaceBytesForDisplayCount(int displayCount)
	{
		return Math.Max(MinimumDiskSpaceBytes, (long)Math.Max(displayCount, 1) * 1000000000L + 1000000000);
	}
}
