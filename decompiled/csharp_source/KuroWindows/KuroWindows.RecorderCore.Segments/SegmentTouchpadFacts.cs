using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentTouchpadFacts
{
	[JsonPropertyName("devices")]
	public int Devices { get; set; }

	[JsonPropertyName("frames")]
	public long Frames { get; set; }

	[JsonPropertyName("reports")]
	public long Reports { get; set; }

	[JsonPropertyName("partial")]
	public long Partial { get; set; }

	[JsonPropertyName("timeouts")]
	public long Timeouts { get; set; }

	[JsonPropertyName("dup_ids")]
	public long DupIds { get; set; }

	[JsonPropertyName("unknown_report_id")]
	public long UnknownReportId { get; set; }

	[JsonPropertyName("unknown_device")]
	public long UnknownDevice { get; set; }

	[JsonPropertyName("len_mismatch")]
	public long LenMismatch { get; set; }

	[JsonPropertyName("raw_fail")]
	public long RawFail { get; set; }

	[JsonPropertyName("buffer_grow")]
	public long BufferGrow { get; set; }

	[JsonPropertyName("contacts_truncated")]
	public long ContactsTruncated { get; set; }

	[JsonPropertyName("gestures")]
	public SegmentTouchpadGestureFacts Gestures { get; set; } = new SegmentTouchpadGestureFacts();

	[JsonPropertyName("scroll_rows")]
	public long ScrollRows { get; set; }

	[JsonPropertyName("os_rows")]
	public long OsRows { get; set; }

	[JsonPropertyName("orphan_wheels")]
	public long OrphanWheels { get; set; }

	[JsonPropertyName("palm_contacts")]
	public long PalmContacts { get; set; }

	[JsonPropertyName("registered")]
	public bool Registered { get; set; }

	[JsonPropertyName("register_fail")]
	public int RegisterFail { get; set; }

	[JsonPropertyName("reregisters")]
	public int Reregisters { get; set; }
}
