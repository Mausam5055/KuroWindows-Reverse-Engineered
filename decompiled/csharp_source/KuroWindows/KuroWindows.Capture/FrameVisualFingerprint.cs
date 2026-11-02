using System;

namespace KuroWindows.Capture;

internal static class FrameVisualFingerprint
{
	private const int GridWidth = 8;

	private const int GridHeight = 8;

	public const int MeaningfulChangeBits = 4;

	public static ulong ComputeSampledHash(byte[] bgra, int width, int height, int stride)
	{
		Span<byte> span = stackalloc byte[64];
		int num = 0;
		int num2 = 0;
		for (int i = 0; i < 8; i++)
		{
			int num3 = Math.Min(height - 1, (i * height + height / 2) / 8) * stride;
			for (int j = 0; j < 8; j++)
			{
				int num4 = Math.Min(width - 1, (j * width + width / 2) / 8);
				int num5 = num3 + num4 * 4;
				byte b = bgra[num5];
				byte b2 = bgra[num5 + 1];
				byte b3 = (byte)((bgra[num5 + 2] * 299 + b2 * 587 + b * 114) / 1000);
				span[num2++] = b3;
				num += b3;
			}
		}
		int num6 = num / span.Length;
		ulong num7 = 0uL;
		for (int k = 0; k < span.Length; k++)
		{
			if (span[k] >= num6)
			{
				num7 |= (ulong)(1L << k);
			}
		}
		return num7;
	}

	public static bool IsMeaningfulChange(int? changeScore)
	{
		if (!changeScore.HasValue || changeScore.GetValueOrDefault() >= 4)
		{
			return true;
		}
		return false;
	}

	public static int HammingDistance(ulong a, ulong b)
	{
		ulong num = a ^ b;
		int num2 = 0;
		while (num != 0L)
		{
			num &= num - 1;
			num2++;
		}
		return num2;
	}
}
