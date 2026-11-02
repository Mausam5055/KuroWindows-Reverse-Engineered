using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class DeviceMetadata
{
	[JsonPropertyName("platform")]
	public string Platform { get; set; } = "windows";

	[JsonPropertyName("os_version")]
	public string OsVersion { get; set; } = "";

	[JsonPropertyName("machine_model")]
	public string MachineModel { get; set; } = "";

	[JsonPropertyName("physical_memory_gb")]
	public long PhysicalMemoryGb { get; set; }

	[JsonPropertyName("cpu_count")]
	public int CpuCount { get; set; }

	[JsonPropertyName("timezone")]
	public string Timezone { get; set; } = "";

	[JsonPropertyName("locale")]
	public string Locale { get; set; } = "";
}
