using System;
using System.Security.Cryptography;

namespace ClientTelemetry;

internal static class UuidV7
{
	public static Guid New(DateTimeOffset now)
	{
		Span<byte> span = stackalloc byte[16];
		RandomNumberGenerator.Fill(span);
		long num = now.ToUnixTimeMilliseconds();
		span[0] = (byte)(num >> 40);
		span[1] = (byte)(num >> 32);
		span[2] = (byte)(num >> 24);
		span[3] = (byte)(num >> 16);
		span[4] = (byte)(num >> 8);
		span[5] = (byte)num;
		span[6] = (byte)((span[6] & 0xF) | 0x70);
		span[8] = (byte)((span[8] & 0x3F) | 0x80);
		ref byte reference = ref span[0];
		ref byte reference2 = ref span[3];
		byte b = span[3];
		byte b2 = span[0];
		reference = b;
		reference2 = b2;
		reference = ref span[1];
		ref byte reference3 = ref span[2];
		b2 = span[2];
		b = span[1];
		reference = b2;
		reference3 = b;
		reference = ref span[4];
		ref byte reference4 = ref span[5];
		b = span[5];
		b2 = span[4];
		reference = b;
		reference4 = b2;
		reference = ref span[6];
		ref byte reference5 = ref span[7];
		b2 = span[7];
		b = span[6];
		reference = b2;
		reference5 = b;
		return new Guid(span);
	}
}
