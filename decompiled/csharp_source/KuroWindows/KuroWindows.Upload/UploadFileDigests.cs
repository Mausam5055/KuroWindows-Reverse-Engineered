using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace KuroWindows.Upload;

internal static class UploadFileDigests
{
	public const long MultipartThresholdBytes = 134217728L;

	public const long MultipartPartSizeBytes = 67108864L;

	private static readonly ConcurrentDictionary<string, (long Size, long MtimeUtcTicks, UploadFileDigest Digest)> Cache = new ConcurrentDictionary<string, (long, long, UploadFileDigest)>(StringComparer.OrdinalIgnoreCase);

	public static UploadFileDigest For(string path)
	{
		FileInfo fileInfo = new FileInfo(path);
		long length = fileInfo.Length;
		long ticks = fileInfo.LastWriteTimeUtc.Ticks;
		if (Cache.TryGetValue(path, out (long, long, UploadFileDigest) value) && value.Item1 == length && value.Item2 == ticks)
		{
			return value.Item3;
		}
		UploadFileDigest uploadFileDigest;
		using (FileStream stream = File.OpenRead(path))
		{
			uploadFileDigest = Compute(stream, (length >= 134217728) ? new long?(67108864L) : ((long?)null));
		}
		Cache[path] = (length, ticks, uploadFileDigest);
		if (Cache.Count > 512)
		{
			foreach (string key in Cache.Keys)
			{
				if (!File.Exists(key))
				{
					Cache.TryRemove(key, out (long, long, UploadFileDigest) _);
				}
			}
		}
		return uploadFileDigest;
	}

	public static void Invalidate(string path)
	{
		Cache.TryRemove(path, out (long, long, UploadFileDigest) _);
	}

	internal static UploadFileDigest Compute(Stream stream, long? partSizeBytes)
	{
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		using IncrementalHash incrementalHash2 = ((!partSizeBytes.HasValue) ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
		List<string> list = ((incrementalHash2 == null) ? null : new List<string>());
		byte[] array = new byte[1048576];
		long num = 0L;
		int num2;
		while ((num2 = stream.Read(array, 0, array.Length)) > 0)
		{
			incrementalHash.AppendData(array, 0, num2);
			if (incrementalHash2 == null)
			{
				continue;
			}
			int num3 = 0;
			while (num3 < num2)
			{
				int num4 = (int)Math.Min(num2 - num3, partSizeBytes.Value - num);
				incrementalHash2.AppendData(array, num3, num4);
				num3 += num4;
				num += num4;
				if (num == partSizeBytes.Value)
				{
					list.Add(Hex(incrementalHash2.GetHashAndReset()));
					num = 0L;
				}
			}
		}
		if (incrementalHash2 != null && num > 0)
		{
			list.Add(Hex(incrementalHash2.GetHashAndReset()));
		}
		return new UploadFileDigest(Hex(incrementalHash.GetHashAndReset()), list);
	}

	private static string Hex(byte[] hash)
	{
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
