using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionFactsTouchpadGestureMetadata
{
	[JsonPropertyName("scroll")]
	public long Scroll { get; set; }

	[JsonPropertyName("pinch")]
	public long Pinch { get; set; }

	[JsonPropertyName("swipe")]
	public long Swipe { get; set; }

	[JsonPropertyName("pan")]
	public long Pan { get; set; }

	[JsonPropertyName("other")]
	public long Other { get; set; }
}
