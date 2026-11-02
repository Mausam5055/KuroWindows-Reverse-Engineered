using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KuroWindows.Input.Uia;

internal static class UiaProbeProtocol
{
	internal const ushort Version = 1;

	internal const int MaxFrameBytes = 8192;

	internal const int MaxTextChars = 500;

	internal const int MaxIdentityChars = 96;

	internal static byte[] EncodeRequest(in UiaProbeRequest request)
	{
		using MemoryStream memoryStream = new MemoryStream(64);
		using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
		{
			binaryWriter.Write((ushort)1);
			binaryWriter.Write(request.Seq);
			binaryWriter.Write((byte)request.Site);
			binaryWriter.Write((byte)request.Reads);
			binaryWriter.Write(Truncate(request.TargetExe, 96));
			binaryWriter.Write(Truncate(request.TargetClass, 96));
			binaryWriter.Write(request.TargetPid);
			binaryWriter.Write(request.TargetWindow);
		}
		return memoryStream.ToArray();
	}

	internal static bool TryDecodeRequest(byte[] payload, out UiaProbeRequest request)
	{
		request = default;
		try
		{
			using MemoryStream input = new MemoryStream(payload, writable: false);
			using BinaryReader binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
			if (binaryReader.ReadUInt16() != 1)
			{
				return false;
			}
			int seq = binaryReader.ReadInt32();
			UiaProbeSite uiaProbeSite = (UiaProbeSite)binaryReader.ReadByte();
			UiaProbeReads uiaProbeReads = (UiaProbeReads)binaryReader.ReadByte();
			string text = binaryReader.ReadString();
			string text2 = binaryReader.ReadString();
			int targetPid = binaryReader.ReadInt32();
			long targetWindow = binaryReader.ReadInt64();
			if (((int)uiaProbeSite < 1 || (int)uiaProbeSite > 3) ? true : false)
			{
				return false;
			}
			if ((uiaProbeReads & ~(UiaProbeReads.SecureField | UiaProbeReads.TextSelection)) != 0)
			{
				return false;
			}
			if (text.Length > 96 || text2.Length > 96)
			{
				return false;
			}
			request = new UiaProbeRequest(seq, uiaProbeSite, uiaProbeReads, text, text2, targetPid, targetWindow);
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static byte[] EncodeResponse(in UiaProbeResponse response)
	{
		using MemoryStream memoryStream = new MemoryStream(64);
		using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
		{
			binaryWriter.Write((ushort)1);
			binaryWriter.Write(response.Seq);
			binaryWriter.Write((byte)response.Status);
			binaryWriter.Write(response.IsSecure);
			binaryWriter.Write(Truncate(response.SelectedText ?? "", 500));
		}
		return memoryStream.ToArray();
	}

	internal static bool TryDecodeResponse(byte[] payload, out UiaProbeResponse response)
	{
		response = default;
		try
		{
			using MemoryStream input = new MemoryStream(payload, writable: false);
			using BinaryReader binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
			if (binaryReader.ReadUInt16() != 1)
			{
				return false;
			}
			int seq = binaryReader.ReadInt32();
			UiaProbeStatus uiaProbeStatus = (UiaProbeStatus)binaryReader.ReadByte();
			bool isSecure = binaryReader.ReadBoolean();
			string text = binaryReader.ReadString();
			if ((int)uiaProbeStatus > 3)
			{
				return false;
			}
			if (text.Length > 500)
			{
				return false;
			}
			response = new UiaProbeResponse(seq, uiaProbeStatus, isSecure, (text.Length == 0) ? null : text);
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
	{
		byte[] array = FrameFor(payload);
		await stream.WriteAsync(array, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
	{
		byte[] header = new byte[4];
		await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		int num = PayloadLength(header);
		byte[] payload = new byte[num];
		await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		return payload;
	}

	internal static void WriteFrame(Stream stream, byte[] payload)
	{
		byte[] array = FrameFor(payload);
		stream.Write(array, 0, array.Length);
		stream.Flush();
	}

	internal static byte[]? ReadFrame(Stream stream)
	{
		byte[] array = new byte[4];
		if (!TryReadExactly(stream, array))
		{
			return null;
		}
		byte[] array2 = new byte[PayloadLength(array)];
		if (!TryReadExactly(stream, array2))
		{
			return null;
		}
		return array2;
	}

	private static byte[] FrameFor(byte[] payload)
	{
		int num = payload.Length;
		if ((num > 8192 || num == 0) ? true : false)
		{
			throw new InvalidDataException($"uia probe frame length {payload.Length} out of range");
		}
		byte[] array = new byte[payload.Length + 4];
		BitConverter.TryWriteBytes(array.AsSpan(0, 4), payload.Length);
		payload.CopyTo(array, 4);
		return array;
	}

	private static int PayloadLength(byte[] header)
	{
		int num = BitConverter.ToInt32(header, 0);
		if ((num <= 0 || num > 8192) ? true : false)
		{
			throw new InvalidDataException($"uia probe frame length {num} out of range");
		}
		return num;
	}

	private static bool TryReadExactly(Stream stream, byte[] destination)
	{
		int num;
		for (int i = 0; i < destination.Length; i += num)
		{
			num = stream.Read(destination, i, destination.Length - i);
			if (num <= 0)
			{
				return false;
			}
		}
		return true;
	}

	internal static string Truncate(string value, int maxChars)
	{
		if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
		{
			return value ?? "";
		}
		int num = maxChars;
		if (char.IsHighSurrogate(value[num - 1]))
		{
			num--;
		}
		return value.Substring(0, num);
	}
}
