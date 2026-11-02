using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Input;

internal sealed class RowSerializer
{
	private static readonly JsonEncodedText X = JsonEncodedText.Encode("x");

	private static readonly JsonEncodedText Y = JsonEncodedText.Encode("y");

	private static readonly JsonEncodedText Button = JsonEncodedText.Encode("button");

	private static readonly JsonEncodedText IsDown = JsonEncodedText.Encode("isDown");

	private static readonly JsonEncodedText ClickCount = JsonEncodedText.Encode("clickCount");

	private static readonly JsonEncodedText Pressure = JsonEncodedText.Encode("pressure");

	private static readonly JsonEncodedText AppName = JsonEncodedText.Encode("appName");

	private static readonly JsonEncodedText BundleId = JsonEncodedText.Encode("bundleId");

	private static readonly JsonEncodedText Screen = JsonEncodedText.Encode("screen");

	private static readonly JsonEncodedText SourcePid = JsonEncodedText.Encode("sourcePid");

	private static readonly JsonEncodedText Timestamp = JsonEncodedText.Encode("timestamp");

	private static readonly JsonEncodedText DeltaX = JsonEncodedText.Encode("deltaX");

	private static readonly JsonEncodedText DeltaY = JsonEncodedText.Encode("deltaY");

	private static readonly JsonEncodedText Phase = JsonEncodedText.Encode("phase");

	private static readonly JsonEncodedText MomentumPhase = JsonEncodedText.Encode("momentumPhase");

	private static readonly JsonEncodedText IsPrecise = JsonEncodedText.Encode("isPrecise");

	private static readonly JsonEncodedText FromScreen = JsonEncodedText.Encode("fromScreen");

	private static readonly JsonEncodedText ToScreen = JsonEncodedText.Encode("toScreen");

	private static readonly JsonEncodedText Dx = JsonEncodedText.Encode("dx");

	private static readonly JsonEncodedText Dy = JsonEncodedText.Encode("dy");

	private static readonly JsonEncodedText Packets = JsonEncodedText.Encode("packets");

	private static readonly JsonEncodedText Modifiers = JsonEncodedText.Encode("modifiers");

	private static readonly JsonEncodedText DisplayIndex = JsonEncodedText.Encode("displayIndex");

	private static readonly JsonEncodedText KeyCode = JsonEncodedText.Encode("keyCode");

	private static readonly JsonEncodedText Characters = JsonEncodedText.Encode("characters");

	private static readonly JsonEncodedText IsRepeat = JsonEncodedText.Encode("isRepeat");

	private static readonly JsonEncodedText Channel = JsonEncodedText.Encode("channel");

	private static readonly JsonEncodedText GestureId = JsonEncodedText.Encode("gestureId");

	private static readonly JsonEncodedText TargetExe = JsonEncodedText.Encode("targetExe");

	private static readonly JsonEncodedText TargetPid = JsonEncodedText.Encode("targetPid");

	private static readonly JsonEncodedText Hwnd = JsonEncodedText.Encode("hwnd");

	private static readonly JsonEncodedText ScanCode = JsonEncodedText.Encode("scanCode");

	private static readonly JsonEncodedText Extended = JsonEncodedText.Encode("extended");

	private static readonly JsonEncodedText Hkl = JsonEncodedText.Encode("hkl");

	private static readonly JsonEncodedText Dead = JsonEncodedText.Encode("dead");

	private static readonly JsonEncodedText ExtraInfo = JsonEncodedText.Encode("extraInfo");

	private static readonly JsonEncodedText LowerIl = JsonEncodedText.Encode("lowerIl");

	private static readonly JsonEncodedText VelocityX = JsonEncodedText.Encode("velocityX");

	private static readonly JsonEncodedText VelocityY = JsonEncodedText.Encode("velocityY");

	private static readonly JsonEncodedText TotalMmX = JsonEncodedText.Encode("totalMmX");

	private static readonly JsonEncodedText TotalMmY = JsonEncodedText.Encode("totalMmY");

	private static readonly JsonEncodedText EndReason = JsonEncodedText.Encode("endReason");

	private static readonly JsonEncodedText Device = JsonEncodedText.Encode("device");

	private static readonly JsonEncodedText ScanTime = JsonEncodedText.Encode("scanTime");

	private static readonly JsonEncodedText ContactCount = JsonEncodedText.Encode("contactCount");

	private static readonly JsonEncodedText Buttons = JsonEncodedText.Encode("buttons");

	private static readonly JsonEncodedText Contacts = JsonEncodedText.Encode("contacts");

	private static readonly JsonEncodedText Frames = JsonEncodedText.Encode("frames");

	private static readonly JsonEncodedText[] ButtonNames = new JsonEncodedText[6]
	{
		JsonEncodedText.Encode(""),
		JsonEncodedText.Encode("left"),
		JsonEncodedText.Encode("right"),
		JsonEncodedText.Encode("middle"),
		JsonEncodedText.Encode("other_3"),
		JsonEncodedText.Encode("other_4")
	};

	private static readonly JsonEncodedText[] ChannelNames = new JsonEncodedText[3]
	{
		JsonEncodedText.Encode("wheel"),
		JsonEncodedText.Encode("touchpad"),
		JsonEncodedText.Encode("os_gesture")
	};

	private static readonly JsonEncodedText[] EndReasonNames = new JsonEncodedText[8]
	{
		JsonEncodedText.Encode(""),
		JsonEncodedText.Encode("lift"),
		JsonEncodedText.Encode("button"),
		JsonEncodedText.Encode("pinch"),
		JsonEncodedText.Encode("fingers"),
		JsonEncodedText.Encode("timeout"),
		JsonEncodedText.Encode("device_removed"),
		JsonEncodedText.Encode("segment_close")
	};

	public static readonly JsonSerializerOptions ColdOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static readonly byte[] SeqMember = ",\"seq\":"u8.ToArray();

	private static readonly byte[] OsTimeMember = ",\"osTime\":"u8.ToArray();

	private readonly ArrayBufferWriter<byte> _buffer = new ArrayBufferWriter<byte>(65536);

	private readonly Utf8JsonWriter _writer;

	public RowSerializer()
	{
		_writer = new Utf8JsonWriter(_buffer, new JsonWriterOptions
		{
			SkipValidation = true,
			Indented = false
		});
	}

	public ReadOnlySpan<byte> Serialize(in EventRow row, long seq)
	{
		switch (row.Kind)
		{
		case RowKind.MouseMove:
			Begin("mouse_move");
			Point(in row);
			App(in row);
			_writer.WriteNumber(Screen, row.Screen);
			Stamp(in row);
			break;
		case RowKind.Drag:
			Begin("drag");
			Point(in row);
			_writer.WriteString(Button, ButtonNames[(uint)row.Button]);
			_writer.WriteNumber(Pressure, 1.0);
			App(in row);
			_writer.WriteNumber(Screen, row.Screen);
			Stamp(in row);
			_writer.WriteNumber(Modifiers, row.Modifiers);
			break;
		case RowKind.Click:
			Begin("click");
			Point(in row);
			_writer.WriteString(Button, ButtonNames[(uint)row.Button]);
			_writer.WriteBoolean(IsDown, (row.Flags & RowFlags.IsDown) != 0);
			_writer.WriteNumber(ClickCount, row.ClickCount);
			_writer.WriteNumber(Pressure, 1.0);
			App(in row);
			_writer.WriteNumber(Screen, row.Screen);
			Stamp(in row);
			_writer.WriteNumber(Modifiers, row.Modifiers);
			Target(in row);
			break;
		case RowKind.Scroll:
		case RowKind.ScrollOs:
			Begin((row.Kind == RowKind.Scroll) ? "scroll" : "scroll_os");
			Point(in row);
			_writer.WriteNumber(DeltaX, row.DeltaX);
			_writer.WriteNumber(DeltaY, row.DeltaY);
			App(in row);
			_writer.WriteNumber(Screen, row.Screen);
			_writer.WriteNumber(Phase, row.Phase);
			_writer.WriteNumber(MomentumPhase, 0);
			_writer.WriteBoolean(IsPrecise, (row.Flags & RowFlags.IsPrecise) != 0);
			Stamp(in row);
			_writer.WriteString(Channel, ChannelNames[(uint)row.Channel]);
			_writer.WriteNumber(Modifiers, row.Modifiers);
			if (row.Channel != ScrollChannel.Wheel)
			{
				_writer.WriteNumber(GestureId, row.GestureId);
			}
			Target(in row);
			if ((row.Flags & RowFlags.Ended) != 0)
			{
				_writer.WriteNumber(VelocityX, row.VelocityX);
				_writer.WriteNumber(VelocityY, row.VelocityY);
				_writer.WriteNumber(TotalMmX, row.TotalMmX);
				_writer.WriteNumber(TotalMmY, row.TotalMmY);
				_writer.WriteString(EndReason, EndReasonNames[(uint)row.EndReason]);
			}
			break;
		case RowKind.ScreenTransition:
			Begin("screen_transition");
			_writer.WriteNumber(FromScreen, row.FromScreen);
			_writer.WriteNumber(ToScreen, row.ToScreen);
			_writer.WriteNumber(Timestamp, row.WallMs);
			return End(seq, row.OsTime);
		case RowKind.MouseDelta:
			Begin("mouse_delta");
			_writer.WriteNumber(Dx, row.Dx);
			_writer.WriteNumber(Dy, row.Dy);
			_writer.WriteNumber(Packets, row.Packets);
			App(in row);
			_writer.WriteNumber(Screen, row.Screen);
			Stamp(in row);
			return End(seq, row.OsTime);
		case RowKind.ModifierChange:
			Begin("modifier_change");
			_writer.WriteNumber(Modifiers, row.Modifiers);
			Display(in row);
			Stamp(in row);
			break;
		case RowKind.Key:
			Begin("key");
			_writer.WriteNumber(KeyCode, row.VkCode);
			_writer.WriteString(Characters, row.Chars ?? "");
			_writer.WriteBoolean(IsDown, (row.Flags & RowFlags.IsDown) != 0);
			_writer.WriteNumber(Modifiers, row.Modifiers);
			App(in row);
			Display(in row);
			_writer.WriteBoolean(IsRepeat, (row.Flags & RowFlags.IsRepeat) != 0);
			Stamp(in row);
			_writer.WriteNumber(ScanCode, row.ScanCode);
			_writer.WriteBoolean(Extended, (row.Flags & RowFlags.Extended) != 0);
			WriteHex(Hkl, (ulong)(row.Hkl & 0xFFFFFFFFu), 8);
			_writer.WriteBoolean(Dead, (row.Flags & RowFlags.Dead) != 0);
			break;
		case RowKind.Touch:
		{
			Begin("touch");
			_writer.WriteNumber(Device, row.Device);
			_writer.WriteNumber(Timestamp, row.WallMs);
			_writer.WriteNumber(ScanTime, row.ScanTime);
			_writer.WriteNumber(ContactCount, row.ContactCount);
			_writer.WriteNumber(Buttons, row.Buttons);
			_writer.WriteStartArray(Contacts);
			for (int i = 0; i < row.ContactsCarried; i++)
			{
				TouchContact touchContact = row.Contacts[i];
				_writer.WriteStartArray();
				_writer.WriteNumberValue(touchContact.Id);
				_writer.WriteNumberValue(touchContact.X);
				_writer.WriteNumberValue(touchContact.Y);
				_writer.WriteNumberValue(touchContact.Flags & 1);
				_writer.WriteNumberValue((touchContact.Flags >> 1) & 1);
				_writer.WriteEndArray();
			}
			_writer.WriteEndArray();
			if ((row.Flags & RowFlags.Coalesced) != 0)
			{
				_writer.WriteNumber(Frames, row.Frames);
			}
			return End(seq, row.OsTime);
		}
		default:
			throw new InvalidOperationException($"no serializer for {row.Kind}");
		}
		if ((row.Flags & RowFlags.HasExtraInfo) != 0)
		{
			WriteHex(ExtraInfo, row.ExtraInfo, 1);
			_writer.WriteBoolean(LowerIl, (row.Flags & RowFlags.LowerIl) != 0);
		}
		return End(seq, row.OsTime);
	}

	public ReadOnlySpan<byte> SerializeCold(object payload, long seq, uint osTime)
	{
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(payload, ColdOptions);
		_buffer.Clear();
		if (array.Length > 2)
		{
			_buffer.Write(array.AsSpan(0, array.Length - 1));
			_buffer.Write(SeqMember);
		}
		else
		{
			_buffer.Write("{\"seq\":"u8);
		}
		WriteNumber(seq);
		_buffer.Write(OsTimeMember);
		WriteNumber(osTime);
		_buffer.Write("}"u8);
		return _buffer.WrittenSpan;
	}

	public ReadOnlySpan<byte> FrameZero(long timestamp, int displayIndex, long seq, uint osTime)
	{
		Begin("frame_zero");
		_writer.WriteNumber("timestamp", timestamp);
		_writer.WriteNumber("displayIndex", displayIndex);
		return End(seq, osTime);
	}

	public ReadOnlySpan<byte> SegmentEnd(long timestamp, string reason, string? maskTrigger, string? maskDetail, int segmentIndex, int displayIndex, long rows, long lastSeq, long seq, uint osTime)
	{
		Begin("segment_end");
		_writer.WriteNumber("timestamp", timestamp);
		_writer.WriteString("reason", reason);
		if (maskTrigger != null)
		{
			_writer.WriteString("maskTrigger", maskTrigger);
		}
		if (maskDetail != null)
		{
			_writer.WriteString("maskDetail", maskDetail);
		}
		_writer.WriteNumber("segmentIndex", segmentIndex);
		_writer.WriteNumber("displayIndex", displayIndex);
		_writer.WriteNumber("rows", rows);
		_writer.WriteNumber("lastSeq", lastSeq);
		return End(seq, osTime);
	}

	private void Point(in EventRow row)
	{
		_writer.WriteNumber(X, row.X);
		_writer.WriteNumber(Y, row.Y);
	}

	private void Stamp(in EventRow row)
	{
		_writer.WriteNumber(SourcePid, row.SourcePid);
		_writer.WriteNumber(Timestamp, row.WallMs);
	}

	private void Target(in EventRow row)
	{
		if ((row.Flags & RowFlags.HasTarget) == 0)
		{
			return;
		}
		AppContext target = row.Target;
		if ((object)target != null)
		{
			if (target.BundleId != "")
			{
				_writer.WriteString(TargetExe, target.BundleId);
			}
			_writer.WriteNumber(TargetPid, target.Pid);
			_writer.WriteNumber(Hwnd, target.Hwnd);
		}
	}

	private void WriteHex(JsonEncodedText name, ulong value, int minDigits)
	{
		Span<byte> span = stackalloc byte[16];
		int num = 0;
		do
		{
			int num2 = (int)(value & 0xF);
			span[15 - num] = (byte)((num2 < 10) ? (48 + num2) : (97 + num2 - 10));
			num++;
			value >>= 4;
		}
		while (value != 0L || num < minDigits);
		_writer.WriteString(name, span.Slice(16 - num, num));
	}

	private void App(in EventRow row)
	{
		AppContext appContext = row.App ?? AppContext.Unknown;
		_writer.WriteString(AppName, appContext.AppName);
		_writer.WriteString(BundleId, appContext.BundleId);
	}

	private void Display(in EventRow row)
	{
		if ((row.Flags & RowFlags.HasDisplayIndex) != 0)
		{
			_writer.WriteNumber(DisplayIndex, row.Display);
		}
	}

	private void Begin(string type)
	{
		_buffer.Clear();
		_writer.Reset(_buffer);
		_writer.WriteStartObject();
		_writer.WriteString("type", type);
	}

	private ReadOnlySpan<byte> End(long seq, uint osTime)
	{
		_writer.WriteNumber("seq", seq);
		_writer.WriteNumber("osTime", osTime);
		_writer.WriteEndObject();
		_writer.Flush();
		return _buffer.WrittenSpan;
	}

	private void WriteNumber(long value)
	{
		Span<byte> span = _buffer.GetSpan(20);
		Utf8Formatter.TryFormat(value, span, out var bytesWritten);
		_buffer.Advance(bytesWritten);
	}
}
