using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentInputFacts
{
	[JsonPropertyName("events")]
	public long Events { get; set; }

	[JsonPropertyName("injected")]
	public long Injected { get; set; }

	[JsonPropertyName("injected_mouse")]
	public long InjectedMouse { get; set; }

	[JsonPropertyName("injected_key")]
	public long InjectedKey { get; set; }

	[JsonPropertyName("extra_info")]
	public List<SegmentInputExtraInfo>? ExtraInfo { get; set; }

	[JsonPropertyName("raw_packets")]
	public long RawPackets { get; set; }

	[JsonPropertyName("raw_null_device")]
	public long RawNullDevice { get; set; }

	[JsonPropertyName("raw_absolute")]
	public long RawAbsolute { get; set; }

	[JsonPropertyName("devices")]
	public List<SegmentInputDevice>? Devices { get; set; }

	[JsonPropertyName("raw_keyboard")]
	public long RawKeyboard { get; set; }

	[JsonPropertyName("raw_failures")]
	public long RawFailures { get; set; }

	[JsonPropertyName("gamepad_packets")]
	public long GamepadPackets { get; set; }

	[JsonPropertyName("rearms")]
	public long Rearms { get; set; }

	[JsonPropertyName("gap_ms")]
	public long GapMs { get; set; }

	[JsonPropertyName("blind_ms")]
	public long BlindMs { get; set; }

	[JsonPropertyName("dropped")]
	public long Dropped { get; set; }

	[JsonPropertyName("late")]
	public long Late { get; set; }

	[JsonPropertyName("thinned")]
	public long Thinned { get; set; }

	[JsonPropertyName("queue_peak")]
	public int QueuePeak { get; set; }

	[JsonPropertyName("witness")]
	public bool Witness { get; set; }

	[JsonPropertyName("touchpad")]
	public SegmentTouchpadFacts? Touchpad { get; set; }
}
