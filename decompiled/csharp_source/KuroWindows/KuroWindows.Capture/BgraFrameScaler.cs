using System;

namespace KuroWindows.Capture;

internal static class BgraFrameScaler
{
	public static void DownscaleNearest(byte[] source, int sourceWidth, int sourceHeight, int sourceStride, byte[] destination, int destinationWidth, int destinationHeight, int destinationStride)
	{
		if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
		{
			Buffer.BlockCopy(source, 0, destination, 0, Math.Min(source.Length, destination.Length));
			return;
		}
		for (int i = 0; i < destinationHeight; i++)
		{
			int num = Math.Min(sourceHeight - 1, (int)((long)i * (long)sourceHeight / destinationHeight)) * sourceStride;
			int num2 = i * destinationStride;
			for (int j = 0; j < destinationWidth; j++)
			{
				int num3 = Math.Min(sourceWidth - 1, (int)((long)j * (long)sourceWidth / destinationWidth));
				int num4 = num + num3 * 4;
				int num5 = num2 + j * 4;
				destination[num5] = source[num4];
				destination[num5 + 1] = source[num4 + 1];
				destination[num5 + 2] = source[num4 + 2];
				destination[num5 + 3] = source[num4 + 3];
			}
		}
	}
}
