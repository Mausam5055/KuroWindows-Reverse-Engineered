using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class SessionFactsDisplayMetadata
{
	[JsonPropertyName("tech")]
	public string? Tech { get; set; }

	[JsonPropertyName("mfr")]
	public string? Mfr { get; set; }

	[JsonPropertyName("pid")]
	public string? Pid { get; set; }

	[JsonPropertyName("serial")]
	public bool Serial { get; set; }

	[JsonPropertyName("adapter")]
	public string? Adapter { get; set; }

	[JsonPropertyName("builtin")]
	public bool Builtin { get; set; }
}
