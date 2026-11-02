using System;

namespace DeviceIdentity;

internal static class Settings
{
	private static DeviceIdentitySettings Current => DeviceIdentityRuntime.Current;

	public static string DeviceKeyName => Current.DeviceKeyName;

	public static string DeviceKeyRsaName => Current.DeviceKeyRsaName;

	public static string PlatformCryptoProviderName => Current.PlatformCryptoProviderName;

	public static string DeviceProofPrefix => Current.DeviceProofPrefix;

	public static string[] DeviceKeyAcknowledgedStatuses => Current.DeviceKeyAcknowledgedStatuses;

	public static TimeSpan EndorsementCertificateBudget => Current.EndorsementCertificateBudget;

	public static TimeSpan HardwareFacetsBudget => Current.HardwareFacetsBudget;

	public static TimeSpan CloudMetadataProbeTimeout => Current.CloudMetadataProbeTimeout;

	public static int HardwareFacetsMaxBytes => Current.HardwareFacetsMaxBytes;

	public static int EndorsementCertificateMaxBase64Chars => Current.EndorsementCertificateMaxBase64Chars;

	public static string DeviceAuthDirectory => Current.DeviceAuthDirectory;

	public static string EndorsementCertificateCacheFileName => Current.EndorsementCertificateCacheFileName;

	public static string EndorsementCertificateAttemptMarkerFileName => Current.EndorsementCertificateAttemptMarkerFileName;

	public static string IntelEndorsementCertificateServiceUrl => Current.IntelEndorsementCertificateServiceUrl;

	public static string AmdEndorsementCertificateServiceUrl => Current.AmdEndorsementCertificateServiceUrl;
}
