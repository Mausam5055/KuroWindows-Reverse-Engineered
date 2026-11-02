using System;
using System.IO;

namespace KuroWindows.RecorderCore.Capture;

internal static class Mp4ContainerInspector
{
	public static bool HasFinalizedContainer(string videoPath)
	{
		bool flag = false;
		bool flag2 = false;
		bool flag3 = false;
		using FileStream fileStream = File.Open(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		using BinaryReader binaryReader = new BinaryReader(fileStream);
		while (fileStream.Position + 8 <= fileStream.Length)
		{
			long position = fileStream.Position;
			uint num = ReadUInt32BigEndian(binaryReader);
			byte[] array = binaryReader.ReadBytes(4);
			if (array.Length != 4)
			{
				KuroLog.LocalOnly("capture", $"mp4 inspect truncated box type file={Path.GetFileName(videoPath)} pos={position} len={fileStream.Length}", "HasFinalizedContainer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\Mp4ContainerInspector.cs", 26);
				return false;
			}
			ulong num2 = num;
			switch (num)
			{
			case 1u:
				if (fileStream.Position + 8 > fileStream.Length)
				{
					KuroLog.LocalOnly("capture", $"mp4 inspect truncated large-size box header file={Path.GetFileName(videoPath)} pos={position} len={fileStream.Length}", "HasFinalizedContainer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\Mp4ContainerInspector.cs", 37);
					return false;
				}
				num2 = ReadUInt64BigEndian(binaryReader);
				break;
			case 0u:
				num2 = (ulong)(fileStream.Length - position);
				break;
			}
			ulong num3 = (ulong)(fileStream.Position - position);
			if (num2 < num3 || num2 > (ulong)(fileStream.Length - position))
			{
				KuroLog.LocalOnly("capture", $"mp4 inspect malformed box file={Path.GetFileName(videoPath)} pos={position} size={num2} len={fileStream.Length}", "HasFinalizedContainer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\Mp4ContainerInspector.cs", 52);
				return false;
			}
			flag |= IsBoxType(array, 'f', 't', 'y', 'p');
			flag2 |= IsBoxType(array, 'm', 'o', 'o', 'v');
			flag3 |= IsBoxType(array, 'm', 'd', 'a', 't') && num2 > num3;
			fileStream.Position = position + (long)num2;
		}
		bool num4 = flag & flag2 & flag3;
		if (!num4)
		{
			KuroLog.LocalOnly("capture", $"mp4 inspect not finalized file={Path.GetFileName(videoPath)} ftyp={flag} moov={flag2} mdat={flag3} len={fileStream.Length}", "HasFinalizedContainer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\Mp4ContainerInspector.cs", 69);
		}
		return num4;
	}

	private static bool IsBoxType(byte[] typeBytes, char a, char b, char c, char d)
	{
		if (typeBytes[0] == (byte)a && typeBytes[1] == (byte)b && typeBytes[2] == (byte)c)
		{
			return typeBytes[3] == (byte)d;
		}
		return false;
	}

	private static uint ReadUInt32BigEndian(BinaryReader reader)
	{
		byte[] array = reader.ReadBytes(4);
		if (array.Length != 4)
		{
			throw new EndOfStreamException();
		}
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(array);
		}
		return BitConverter.ToUInt32(array);
	}

	private static ulong ReadUInt64BigEndian(BinaryReader reader)
	{
		byte[] array = reader.ReadBytes(8);
		if (array.Length != 8)
		{
			throw new EndOfStreamException();
		}
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(array);
		}
		return BitConverter.ToUInt64(array);
	}
}
