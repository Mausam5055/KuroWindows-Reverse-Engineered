using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DeviceIdentity;

internal static class DeviceProof
{
	internal readonly record struct Headers(string KeyId, string Timestamp, string Signature);

	private static long _clockOffsetSeconds;

	public static long ClockOffsetSeconds => Interlocked.Read(in _clockOffsetSeconds);

	public static string BodyDigestHex(ReadOnlySpan<byte> body)
	{
		return Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
	}

	public static string CanonicalMessage(string userId, string timestamp, string bodySha256Hex)
	{
		return $"{Settings.DeviceProofPrefix}\n{userId.Trim().ToLowerInvariant()}\n{timestamp}\n{bodySha256Hex}";
	}

	public static Headers Build(DeviceKey.Snapshot key, string userId, long unixSeconds, ReadOnlySpan<byte> body)
	{
		return BuildForDigest(key, userId, unixSeconds, BodyDigestHex(body));
	}

	public static Headers BuildForDigest(DeviceKey.Snapshot key, string userId, long unixSeconds, string bodySha256Hex)
	{
		string timestamp = unixSeconds.ToString(CultureInfo.InvariantCulture);
		byte[] bytes = Encoding.UTF8.GetBytes(CanonicalMessage(userId, timestamp, bodySha256Hex));
		byte[] inArray = key.Sign(bytes);
		return new Headers(key.KeyId, timestamp, Convert.ToBase64String(inArray));
	}

	public static long LearnClockOffset(long serverUnixSeconds)
	{
		long num = serverUnixSeconds - LocalUnixSeconds();
		Interlocked.Exchange(ref _clockOffsetSeconds, num);
		return num;
	}

	internal static void ResetClockOffset()
	{
		Interlocked.Exchange(ref _clockOffsetSeconds, 0L);
	}

	public static long NowUnixSeconds()
	{
		return LocalUnixSeconds() + ClockOffsetSeconds;
	}

	private static long LocalUnixSeconds()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
	}
}
