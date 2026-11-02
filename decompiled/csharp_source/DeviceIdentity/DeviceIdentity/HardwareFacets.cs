using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DeviceIdentity;

internal static class HardwareFacets
{
	private sealed record MetadataAnswer(string Probe, bool Responded, int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body);

	internal static class CloudMetadataClassifier
	{
		public const string Aws = "aws";

		public const string Azure = "azure";

		public const string Gcp = "gcp";

		public const string Oci = "oci";

		public const string Unknown = "unknown";

		public const string AwsTokenTtlHeader = "X-aws-ec2-metadata-token-ttl-seconds";

		public static string Resolve(IEnumerable<(string Probe, int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body)> answers)
		{
			foreach (var answer in answers)
			{
				string item = answer.Probe;
				int item2 = answer.Status;
				IReadOnlyList<KeyValuePair<string, string>> item3 = answer.Headers;
				string item4 = answer.Body;
				string text = Identify(item, item2, item3, item4);
				if (text != null)
				{
					return text;
				}
			}
			return "unknown";
		}

		public static string? Identify(string probe, int status, IReadOnlyList<KeyValuePair<string, string>> headers, string body)
		{
			if (HeaderEquals(headers, "Server", "EC2ws"))
			{
				return "aws";
			}
			if (HeaderEquals(headers, "Metadata-Flavor", "Google"))
			{
				return "gcp";
			}
			bool flag = status >= 200 && status <= 299;
			switch (probe)
			{
			case "aws":
				if (!flag || !HasHeader(headers, "X-aws-ec2-metadata-token-ttl-seconds") || !IsToken(body))
				{
					return null;
				}
				return "aws";
			case "azure":
				if (flag && IsJsonObject(body) && HasObjectField(body, "compute"))
				{
					return "azure";
				}
				if (status == 400 && IsJsonObject(body) && body.Contains("Required metadata header", StringComparison.OrdinalIgnoreCase))
				{
					return "azure";
				}
				return null;
			case "oci":
				if (!flag || !IsJsonObject(body))
				{
					return null;
				}
				if (StringFields(body, "canonicalRegionName").Any())
				{
					return "oci";
				}
				if (!StringFields(body, "id").Any((string v) => v.StartsWith("ocid1.", StringComparison.Ordinal)))
				{
					return null;
				}
				return "oci";
			default:
				return null;
			}
		}

		private static bool HasHeader(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
		{
			return headers.Any((KeyValuePair<string, string> h) => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
		}

		private static bool HeaderEquals(IReadOnlyList<KeyValuePair<string, string>> headers, string name, string value)
		{
			return headers.Any((KeyValuePair<string, string> h) => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase) && string.Equals(h.Value.Trim(), value, StringComparison.OrdinalIgnoreCase));
		}

		private static bool IsToken(string body)
		{
			string text = body.Trim(' ', '\t', '\r', '\n');
			if (text.Length == 0 || text[0] == '{' || text[0] == '[')
			{
				return false;
			}
			return text.All(IsTokenChar);
		}

		private static bool IsTokenChar(char c)
		{
			if ((c < 'A' || c > 'Z') && (c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '+' && c != '/' && c != '=' && c != '_')
			{
				return c == '-';
			}
			return true;
		}

		private static bool IsJsonObject(string body)
		{
			return body.TrimStart().StartsWith('{');
		}

		private static IEnumerable<int> ValueStarts(string body, string key)
		{
			string token = "\"" + key + "\"";
			int from = 0;
			while (from <= body.Length - token.Length)
			{
				int num = body.IndexOf(token, from, StringComparison.Ordinal);
				if (num < 0)
				{
					break;
				}
				from = num + 1;
				int i;
				for (i = num + token.Length; i < body.Length && char.IsWhiteSpace(body[i]); i++)
				{
				}
				if (i < body.Length && body[i] == ':')
				{
					for (i++; i < body.Length && char.IsWhiteSpace(body[i]); i++)
					{
					}
					if (i < body.Length)
					{
						yield return i;
					}
				}
			}
		}

		private static bool HasObjectField(string body, string key)
		{
			return ValueStarts(body, key).Any((int i) => body[i] == '{');
		}

		private static IEnumerable<string> StringFields(string body, string key)
		{
			foreach (int item in ValueStarts(body, key))
			{
				if (body[item] == '"')
				{
					int num = body.IndexOf('"', item + 1);
					string text;
					if (num >= 0)
					{
						int num2 = item + 1;
						text = body.Substring(num2, num - num2);
					}
					else
					{
						text = body.Substring(item + 1);
					}
					yield return text;
				}
			}
		}
	}

	private sealed class GroupFailed
	{
		public static readonly GroupFailed Instance = new GroupFailed();
	}

	private struct SystemPowerStatus
	{
		public byte ACLineStatus;

		public byte BatteryFlag;

		public byte BatteryLifePercent;

		public byte SystemStatusFlag;

		public uint BatteryLifeTime;

		public uint BatteryFullLifeTime;
	}

	public const int Version = 1;

	private const int MaxErrors = 4;

	private const int MaxErrorChars = 32;

	private static readonly JsonSerializerOptions Compact = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static readonly object StartLock = new object();

	private static Task<JsonElement?>? _collection;

	private static int _lastBytes;

	private static readonly Action<Dictionary<string, object?>>[] Reductions = new Action<Dictionary<string, object>>[19]
	{
		(Dictionary<string, object?> b) =>
		{
			TrimList(b, "displays", 2);
		},
		(Dictionary<string, object?> b) =>
		{
			TrimList(b, "pci_vendors", 8);
		},
		(Dictionary<string, object?> b) =>
		{
			TrimNestedList(b, "acpi", "tables", 8);
		},
		(Dictionary<string, object?> b) =>
		{
			TrimList(b, "gpus", 2);
		},
		(Dictionary<string, object?> b) =>
		{
			TrimList(b, "disks", 2);
		},
		(Dictionary<string, object?> b) =>
		{
			TrimNestedList(b, "session", "remote_tools", 4);
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("disks");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("gpus");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("displays");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("acpi");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("smbios");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("cpu");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("pci_vendors");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("session");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("chassis");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("power");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("hypervisor");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("tpm");
		},
		(Dictionary<string, object?> b) =>
		{
			b.Remove("imds");
		}
	};

	private const uint CmGetIdListFilterEnumerator = 1u;

	private const uint CmGetIdListFilterPresent = 256u;

	private const uint AcpiFirmwareProvider = 1094930505u;

	public static int LastBytes => Volatile.Read(in _lastBytes);

	public static void WarmUp()
	{
		Start();
	}

	public static async Task<JsonElement?> CollectAsync(TimeSpan budget)
	{
		Task<JsonElement?> collection = Start();
		if (!collection.IsCompleted && await Task.WhenAny(collection, Task.Delay(budget)).ConfigureAwait(continueOnCapturedContext: false) != collection)
		{
			return null;
		}
		return await collection.ConfigureAwait(continueOnCapturedContext: false);
	}

	private static Task<JsonElement?> Start()
	{
		lock (StartLock)
		{
			return _collection ?? (_collection = Task.Run((Func<Task<JsonElement?>?>)CollectOnceAsync));
		}
	}

	private static async Task<JsonElement?> CollectOnceAsync()
	{
		Stopwatch clock = Stopwatch.StartNew();
		try
		{
			(string Name, Task<object?> Task)[] groups = new (string, Task<object>)[14]
			{
				("hypervisor", Task.Run((Func<object>)Hypervisor)),
				("cpu", Task.Run((Func<object>)Cpu)),
				("pci_vendors", Task.Run((Func<object>)PciVendors)),
				("acpi", Task.Run((Func<object>)Acpi)),
				("smbios", Task.Run((Func<object>)Smbios)),
				("chassis", Task.Run((Func<object>)Chassis)),
				("gpus", Task.Run(() => WmiList("Win32_VideoController", "Name", 4, 64))),
				("disks", Task.Run((Func<object>)Disks)),
				("system_disk", Task.Run((Func<object>)SystemDisk)),
				("tpm", Task.Run((Func<object>)Tpm)),
				("power", Task.Run((Func<object>)Power)),
				("session", Task.Run((Func<object>)Session)),
				("imds", CloudMetadataAsync()),
				("displays", Task.Run((Func<object>)Displays))
			};
			await Task.WhenAny(Task.WhenAll(groups.Select(((string Name, Task<object> Task) g) => g.Task)), Task.Delay(Settings.HardwareFacetsBudget)).ConfigureAwait(continueOnCapturedContext: false);
			Dictionary<string, object> dictionary = new Dictionary<string, object> { ["v"] = 1 };
			List<string> list = new List<string>();
			(string, Task<object>)[] array = groups;
			for (int num = 0; num < array.Length; num++)
			{
				var (text, task) = array[num];
				if (task.IsCompletedSuccessfully && !(task.Result is GroupFailed))
				{
					if (task.Result != null)
					{
						dictionary[text] = task.Result;
					}
					continue;
				}
				_ = task.Exception;
				if (list.Count < 4)
				{
					list.Add(Text(task.IsCompleted ? text : (text + "_timeout"), 32));
				}
			}
			dictionary["ms"] = (int)Math.Min(clock.ElapsedMilliseconds, 2147483647L);
			if (list.Count > 0)
			{
				dictionary["err"] = list;
			}
			JsonElement? result = Bound(dictionary);
			Log.Auth($"hardware facets collected ms={clock.ElapsedMilliseconds} groups={dictionary.Count - ((list.Count > 0) ? 3 : 2)} err={((list.Count > 0) ? string.Join(",", list) : "-")} bytes={LastBytes}", "CollectOnceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\HardwareFacets.cs", 134);
			return result;
		}
		catch (Exception ex)
		{
			Log.Auth("hardware facets collection failed: " + ex.GetType().Name + ": " + ex.Message, "CollectOnceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\HardwareFacets.cs", 139);
			return null;
		}
	}

	private static JsonElement? Bound(Dictionary<string, object?> bundle)
	{
		StripNulls(bundle);
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(bundle, Compact);
		int num = 0;
		while (array.Length > Settings.HardwareFacetsMaxBytes && num < Reductions.Length)
		{
			Reductions[num++](bundle);
			array = JsonSerializer.SerializeToUtf8Bytes(bundle, Compact);
		}
		if (array.Length > Settings.HardwareFacetsMaxBytes)
		{
			Volatile.Write(ref _lastBytes, 0);
			return null;
		}
		Volatile.Write(ref _lastBytes, array.Length);
		using JsonDocument jsonDocument = JsonDocument.Parse(array);
		return jsonDocument.RootElement.Clone();
	}

	private static void StripNulls(object? node)
	{
		if (!(node is Dictionary<string, object> dictionary))
		{
			if (!(node is IList list))
			{
				return;
			}
			for (int num = list.Count - 1; num >= 0; num--)
			{
				if (list[num] == null)
				{
					list.RemoveAt(num);
				}
				else
				{
					StripNulls(list[num]);
				}
			}
			return;
		}
		foreach (string item in dictionary.Keys.ToList())
		{
			if (dictionary[item] == null)
			{
				dictionary.Remove(item);
			}
			else
			{
				StripNulls(dictionary[item]);
			}
		}
	}

	private static void TrimList(Dictionary<string, object?> bundle, string key, int keep)
	{
		if (bundle.TryGetValue(key, out object value) && value is IList list)
		{
			while (list.Count > keep)
			{
				list.RemoveAt(list.Count - 1);
			}
		}
	}

	private static void TrimNestedList(Dictionary<string, object?> bundle, string group, string key, int keep)
	{
		if (bundle.TryGetValue(group, out object value) && value is Dictionary<string, object> bundle2)
		{
			TrimList(bundle2, key, keep);
		}
	}

	private static object? Hypervisor()
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		if (X86Base.IsSupported)
		{
			bool flag = (X86Base.CpuId(1, 0).Ecx & int.MinValue) != 0;
			dictionary["cpuid"] = flag;
			if (flag)
			{
				(int Eax, int Ebx, int Ecx, int Edx) tuple = X86Base.CpuId(1073741824, 0);
				int item = tuple.Eax;
				int item2 = tuple.Ebx;
				int item3 = tuple.Ecx;
				int item4 = tuple.Edx;
				dictionary["vendor"] = Text(Registers(item2, item3, item4), 12);
				if ((uint)item >= 1073741827u)
				{
					(int Eax, int Ebx, int Ecx, int Edx) tuple2 = X86Base.CpuId(1073741827, 0);
					int item5 = tuple2.Eax;
					int item6 = tuple2.Ebx;
					dictionary["leaf3"] = $"{(uint)item5:x8}:{(uint)item6:x8}";
				}
			}
		}
		if (bool.TryParse(Wmi.FirstValue("Win32_ComputerSystem", "HypervisorPresent"), out var result))
		{
			dictionary["wmi"] = result;
		}
		if (dictionary.Count <= 0)
		{
			return null;
		}
		return dictionary;
	}

	private static object? Cpu()
	{
		if (!X86Base.IsSupported)
		{
			return null;
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object> { ["vendor"] = Text(TpmEndorsement.CpuVendor(), 12) };
		if ((uint)X86Base.CpuId(int.MinValue, 0).Eax >= 2147483652u)
		{
			StringBuilder stringBuilder = new StringBuilder(48);
			uint[] array = new uint[3] { 2147483650u, 2147483651u, 2147483652u };
			for (int i = 0; i < array.Length; i++)
			{
				var (num, num2, num3, num4) = X86Base.CpuId((int)array[i], 0);
				stringBuilder.Append(Registers(num, num2, num3, num4));
			}
			dictionary["brand"] = Text(stringBuilder.ToString(), 64);
		}
		return dictionary;
	}

	private static object? PciVendors()
	{
		SortedSet<string> sortedSet = new SortedSet<string>(StringComparer.Ordinal);
		foreach (string item in DeviceIds("PCI"))
		{
			int num = item.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
			if (num >= 0 && num + 8 <= item.Length)
			{
				string text = item.Substring(num + 4, 4).ToUpperInvariant();
				if (text.All(Uri.IsHexDigit))
				{
					sortedSet.Add(text);
				}
			}
		}
		return sortedSet.Take(16).ToList();
	}

	private static object? Acpi()
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		List<string> list = new List<string>();
		foreach (string item in EnumAcpiSignatures())
		{
			if (list.Count >= 16)
			{
				break;
			}
			string text = Text(item, 4);
			if (text != null && !list.Contains(text))
			{
				list.Add(text);
			}
		}
		if (list.Count > 0)
		{
			dictionary["tables"] = list;
		}
		byte[] array = ReadAcpiTable("FACP");
		if (array != null && array.Length >= 16)
		{
			dictionary["oem_id"] = Text(Encoding.ASCII.GetString(array, 10, 6), 6);
		}
		bool flag = DeviceIds("ACPI").Any((string id) => id.Contains("VMGENCTR", StringComparison.OrdinalIgnoreCase) || id.Contains("VM_GEN_COUNTER", StringComparison.OrdinalIgnoreCase));
		dictionary["vmgenid"] = flag;
		return dictionary;
	}

	private static object? Smbios()
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		Dictionary<string, string> dictionary2 = WmiRow("Win32_ComputerSystemProduct", "Vendor", "Name");
		dictionary["vendor"] = Text(dictionary2.GetValueOrDefault("Vendor"), 64);
		dictionary["product"] = Text(dictionary2.GetValueOrDefault("Name"), 64);
		Dictionary<string, string> dictionary3 = WmiRow("Win32_BaseBoard", "Product", "SerialNumber");
		dictionary["board_product"] = Text(dictionary3.GetValueOrDefault("Product"), 64);
		dictionary["board_serial_sha256"] = Hash(dictionary3.GetValueOrDefault("SerialNumber"));
		Dictionary<string, string> dictionary4 = WmiRow("Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion", "SerialNumber");
		dictionary["bios_vendor"] = Text(dictionary4.GetValueOrDefault("Manufacturer"), 64);
		dictionary["bios_version"] = Text(dictionary4.GetValueOrDefault("SMBIOSBIOSVersion"), 32);
		dictionary["system_serial_sha256"] = Hash(dictionary4.GetValueOrDefault("SerialNumber"));
		if (!dictionary.Values.Any((object v) => v != null))
		{
			return null;
		}
		return dictionary;
	}

	private static object? Chassis()
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				using (item)
				{
					if (!(item["ChassisTypes"] is Array { Length: >0 } array) || !(array.GetValue(0) is IConvertible value))
					{
						continue;
					}
					int num = Convert.ToInt32(value, CultureInfo.InvariantCulture);
					if ((num < 0 || num > 255) ? true : false)
					{
						return null;
					}
					return new Dictionary<string, object>
					{
						["type"] = ChassisType(num),
						["smbios_code"] = num
					};
				}
			}
			return null;
		}
		catch
		{
			return GroupFailed.Instance;
		}
	}

	private static string ChassisType(int code)
	{
		switch (code)
		{
		case 8:
		case 9:
		case 10:
		case 14:
		case 31:
		case 32:
			return "laptop";
		case 3:
		case 4:
		case 5:
		case 6:
		case 7:
		case 15:
		case 16:
		case 35:
		case 36:
			return "desktop";
		case 11:
		case 30:
			return "tablet";
		case 17:
		case 18:
		case 19:
		case 20:
		case 21:
		case 22:
		case 23:
		case 24:
			return "server";
		case 1:
			return "other";
		case 2:
			return "unknown";
		default:
			return "unknown";
		}
	}

	private static object? Disks()
	{
		return WmiList("Win32_DiskDrive", "Model", 4, 64, "Model,MediaType,InterfaceType", IsFixedMedia);
	}

	private static object? SystemDisk()
	{
		string text = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		string valueOrDefault = WmiFirst("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + text + "'} WHERE AssocClass=Win32_LogicalDiskToPartition", "DeviceID").GetValueOrDefault("DeviceID");
		if (valueOrDefault == null)
		{
			return null;
		}
		Dictionary<string, string> dictionary = WmiFirst("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + valueOrDefault + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition", "SerialNumber", "Model");
		return DiskIdentity(dictionary.GetValueOrDefault("SerialNumber"), dictionary.GetValueOrDefault("Model"));
	}

	internal static string? DiskIdentity(string? serial, string? model)
	{
		string text = serial?.Trim();
		if (string.IsNullOrEmpty(text) || text.Length < 6 || text.All((char c) => c == text[0]) || string.Equals(text, model?.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return Hash(text);
	}

	private static bool IsFixedMedia(ManagementObject disk)
	{
		string text = disk["MediaType"]?.ToString();
		if (!string.IsNullOrEmpty(text))
		{
			return text.Contains("Fixed", StringComparison.OrdinalIgnoreCase);
		}
		return !string.Equals(disk["InterfaceType"]?.ToString(), "USB", StringComparison.OrdinalIgnoreCase);
	}

	private static object? Tpm()
	{
		TpmEndorsement.TbsProbe tbs = TpmEndorsement.Tbs;
		if (!tbs.Present.HasValue && tbs.Version == null)
		{
			return null;
		}
		return new Dictionary<string, object>
		{
			["present"] = tbs.Present,
			["version"] = Text(tbs.Version, 8)
		};
	}

	private static object? Power()
	{
		if (!GetSystemPowerStatus(out var status))
		{
			return GroupFailed.Instance;
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		if (status.BatteryFlag != byte.MaxValue)
		{
			dictionary["battery"] = (status.BatteryFlag & 0x80) == 0;
		}
		byte aCLineStatus = status.ACLineStatus;
		if ((uint)aCLineStatus <= 1u)
		{
			dictionary["ac"] = status.ACLineStatus == 1;
		}
		if (dictionary.Count <= 0)
		{
			return null;
		}
		return dictionary;
	}

	private static object? Session()
	{
		return new Dictionary<string, object>
		{
			["remote"] = RemoteControlProbe.IsRemoteSession,
			["secure_boot"] = SecureBoot(),
			["remote_tools"] = (from t in RemoteControlProbe.Scan()
				select Text(t, 32) into t
				where t != null
				select t).ToList()
		};
	}

	private static object? Displays()
	{
		IReadOnlyList<DisplayFact> readOnlyList = DisplayFacts.Enumerate();
		if (readOnlyList.Count != 0)
		{
			return (from d in readOnlyList.Take(4)
				select d.ToWire()).ToList();
		}
		return null;
	}

	private static async Task<object?> CloudMetadataAsync()
	{
		try
		{
			using HttpClientHandler handler = new HttpClientHandler
			{
				UseProxy = false,
				AllowAutoRedirect = false
			};
			using HttpClient http = new HttpClient(handler)
			{
				Timeout = Settings.CloudMetadataProbeTimeout
			};
			List<MetadataAnswer> list = (await Task.WhenAll<MetadataAnswer>(ProbeAsync(http, "aws", () =>
			{
				HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
				httpRequestMessage.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "21600");
				return httpRequestMessage;
			}), ProbeAsync(http, "azure", () =>
			{
				HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/metadata/instance?api-version=2021-02-01");
				httpRequestMessage.Headers.TryAddWithoutValidation("Metadata", "true");
				return httpRequestMessage;
			}), ProbeAsync(http, "gcp", () =>
			{
				HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/computeMetadata/v1/instance/id");
				httpRequestMessage.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
				return httpRequestMessage;
			}), ProbeAsync(http, "oci", () =>
			{
				HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/opc/v2/instance/");
				httpRequestMessage.Headers.TryAddWithoutValidation("Authorization", "Bearer Oracle");
				return httpRequestMessage;
			})).ConfigureAwait(continueOnCapturedContext: false)).Where((MetadataAnswer a) => a.Responded).ToList();
			Dictionary<string, object> dictionary = new Dictionary<string, object> { ["responded"] = list.Count > 0 };
			if (list.Count > 0)
			{
				dictionary["cloud"] = CloudMetadataClassifier.Resolve(list.Select((MetadataAnswer a) => (Probe: a.Probe, Status: a.Status, Headers: a.Headers, Body: a.Body)));
			}
			return dictionary;
		}
		catch
		{
			return null;
		}
	}

	private static async Task<MetadataAnswer> ProbeAsync(HttpClient http, string probe, Func<HttpRequestMessage> build)
	{
		_ = 2;
		try
		{
			using HttpRequestMessage request = build();
			using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(continueOnCapturedContext: false);
			string body = "";
			try
			{
				using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(continueOnCapturedContext: false);
				byte[] buffer = new byte[4096];
				int count = await stream.ReadAsync(buffer).ConfigureAwait(continueOnCapturedContext: false);
				body = Encoding.ASCII.GetString(buffer, 0, count);
			}
			catch
			{
			}
			List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
			foreach (KeyValuePair<string, IEnumerable<string>> item in response.Headers.Concat(response.Content.Headers))
			{
				foreach (string item2 in item.Value)
				{
					list.Add(new KeyValuePair<string, string>(item.Key, item2));
				}
			}
			return new MetadataAnswer(probe, Responded: true, (int)response.StatusCode, list, body);
		}
		catch
		{
			return new MetadataAnswer(probe, Responded: false, 0, Array.Empty<KeyValuePair<string, string>>(), "");
		}
	}

	private static bool? SecureBoot()
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State", writable: false);
			return (registryKey?.GetValue("UEFISecureBootEnabled") is int num) ? new bool?(num != 0) : ((bool?)null);
		}
		catch
		{
			return null;
		}
	}

	private static Dictionary<string, string?> WmiRow(string className, params string[] properties)
	{
		return WmiFirst("SELECT " + string.Join(",", properties) + " FROM " + className, properties);
	}

	private static Dictionary<string, string?> WmiFirst(string query, params string[] properties)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher(query);
			using ManagementObjectCollection.ManagementObjectEnumerator managementObjectEnumerator = managementObjectSearcher.Get().GetEnumerator();
			if (managementObjectEnumerator.MoveNext())
			{
				ManagementObject managementObject = (ManagementObject)managementObjectEnumerator.Current;
				using (managementObject)
				{
					foreach (string text in properties)
					{
						dictionary[text] = managementObject[text]?.ToString();
					}
				}
			}
		}
		catch
		{
		}
		return dictionary;
	}

	private static object? WmiList(string className, string property, int max, int maxChars, string? select = null, Func<ManagementObject, bool>? keep = null)
	{
		try
		{
			List<string> list = new List<string>(max);
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT " + (select ?? property) + " FROM " + className);
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				using (item)
				{
					if (list.Count >= max)
					{
						break;
					}
					if (keep == null || keep(item))
					{
						string text = Text(item[property]?.ToString(), maxChars);
						if (text != null && !list.Contains(text))
						{
							list.Add(text);
						}
					}
					continue;
				}
			}
			return list;
		}
		catch
		{
			return GroupFailed.Instance;
		}
	}

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern uint CM_Get_Device_ID_ListW(string filter, char[] buffer, uint bufferLength, uint flags);

	private static List<string> DeviceIds(string enumerator)
	{
		List<string> list = new List<string>();
		try
		{
			if (CM_Get_Device_ID_List_SizeW(out var length, enumerator, 257u) != 0 || length == 0 || length > 1048576)
			{
				return list;
			}
			char[] array = new char[length];
			if (CM_Get_Device_ID_ListW(enumerator, array, length, 257u) != 0)
			{
				return list;
			}
			int num = 0;
			for (int i = 0; i < array.Length; i++)
			{
				if (array[i] == '\0')
				{
					if (i > num)
					{
						list.Add(new string(array, num, i - num));
					}
					num = i + 1;
				}
			}
		}
		catch
		{
		}
		return list;
	}

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

	[DllImport("kernel32.dll")]
	private static extern uint EnumSystemFirmwareTables(uint provider, byte[]? buffer, uint bufferSize);

	[DllImport("kernel32.dll")]
	private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint bufferSize);

	private static List<string> EnumAcpiSignatures()
	{
		List<string> list = new List<string>();
		try
		{
			uint num = EnumSystemFirmwareTables(1094930505u, null, 0u);
			if (num == 0 || num > 65536)
			{
				return list;
			}
			byte[] array = new byte[num];
			uint val = EnumSystemFirmwareTables(1094930505u, array, num);
			for (int i = 0; i + 4 <= Math.Min(val, num); i += 4)
			{
				list.Add(Encoding.ASCII.GetString(array, i, 4));
			}
		}
		catch
		{
		}
		return list;
	}

	private static byte[]? ReadAcpiTable(string signature)
	{
		try
		{
			uint tableId = BitConverter.ToUInt32(Encoding.ASCII.GetBytes(signature), 0);
			uint systemFirmwareTable = GetSystemFirmwareTable(1094930505u, tableId, null, 0u);
			if (systemFirmwareTable == 0 || systemFirmwareTable > 1048576)
			{
				return null;
			}
			byte[] array = new byte[systemFirmwareTable];
			return (GetSystemFirmwareTable(1094930505u, tableId, array, systemFirmwareTable) == 0) ? null : array;
		}
		catch
		{
			return null;
		}
	}

	internal static string? Text(string? value, int max)
	{
		if (string.IsNullOrEmpty(value))
		{
			return null;
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length);
		bool flag = true;
		foreach (char c in value)
		{
			if (c >= '!' && c <= '~')
			{
				stringBuilder.Append(c);
				flag = false;
			}
			else if (c == ' ' || char.IsWhiteSpace(c) || c == '\0')
			{
				if (!flag)
				{
					stringBuilder.Append(' ');
				}
				flag = true;
			}
		}
		string text = stringBuilder.ToString().Trim();
		if (text.Length == 0)
		{
			return null;
		}
		if (text.Length > max)
		{
			return text.Substring(0, max).TrimEnd();
		}
		return text;
	}

	private static string? Hash(string? value)
	{
		string text = value?.Trim();
		if (!string.IsNullOrEmpty(text))
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
		}
		return null;
	}

	private static string Registers(params int[] registers)
	{
		byte[] array = new byte[registers.Length * 4];
		for (int i = 0; i < registers.Length; i++)
		{
			BitConverter.GetBytes(registers[i]).CopyTo(array, i * 4);
		}
		return Encoding.ASCII.GetString(array).Replace("\0", "");
	}
}
