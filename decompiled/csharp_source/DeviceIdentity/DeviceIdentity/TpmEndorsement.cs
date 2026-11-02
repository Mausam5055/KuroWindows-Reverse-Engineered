using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DeviceIdentity;

internal static class TpmEndorsement
{
	internal sealed record TbsProbe(bool? Present, string? Version);

	internal sealed record EndorsementKey(string Sha256Hex, bool IsRsa, byte[] Modulus, byte[] Exponent, byte[] X, byte[] Y);

	private struct TpmDeviceInfo
	{
		public uint structVersion;

		public uint tpmVersion;

		public uint tpmInterfaceType;

		public uint tpmImpRevision;
	}

	private const string SourceProvider = "provider";

	private const string SourceRegistry = "registry";

	private const string SourceVendorService = "vendor_service";

	private const string SourceNone = "none";

	private const string EkCertStoreKeyPath = "SYSTEM\\CurrentControlSet\\Services\\TPM\\WMI\\Endorsement\\EKCertStore\\Certificates";

	private const string EndorsementPublicProperty = "PCP_EKPUB";

	private const uint TbsSuccess = 0u;

	private const uint TbsBadParameter = 2150121474u;

	private const uint TbsTpmNotFound = 2150121487u;

	private const uint TpmVersion12 = 1u;

	private const uint TpmVersion20 = 2u;

	private const uint RsaPublicMagic = 826364754u;

	private static readonly Dictionary<uint, int> EccPublicMagicKeyBytes = new Dictionary<uint, int>
	{
		[827540293u] = 32,
		[827016005u] = 32,
		[861094725u] = 48,
		[860570437u] = 48,
		[894649157u] = 66,
		[894124869u] = 66
	};

	private static readonly Regex GenerationPrefix = new Regex("\\b(\\d{1,2})th Gen\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex CoreModel = new Regex("\\bi[3579]-(\\d{4,5})[A-Z]{0,2}\\d?\\b", RegexOptions.Compiled);

	private static readonly Lazy<TbsProbe> TbsLazy = new Lazy<TbsProbe>(ProbeTbs, LazyThreadSafetyMode.ExecutionAndPublication);

	private static readonly Lazy<EndorsementKey?> KeyLazy = new Lazy<EndorsementKey>(ReadEndorsementKey, LazyThreadSafetyMode.ExecutionAndPublication);

	private static readonly object StartLock = new object();

	private static Task<DeviceChipClaim?>? _chip;

	public static TbsProbe Tbs => TbsLazy.Value;

	public static EndorsementKey? Key => KeyLazy.Value;

	public static void WarmUp()
	{
		Start();
	}

	public static async Task<DeviceChipClaim?> GetChipClaimAsync(TimeSpan budget)
	{
		Task<DeviceChipClaim?> chip = Start();
		if (!chip.IsCompleted && await Task.WhenAny(chip, Task.Delay(budget)).ConfigureAwait(continueOnCapturedContext: false) != chip)
		{
			return null;
		}
		return await chip.ConfigureAwait(continueOnCapturedContext: false);
	}

	private static Task<DeviceChipClaim?> Start()
	{
		lock (StartLock)
		{
			return _chip ?? (_chip = Task.Run((Func<DeviceChipClaim>)BuildChipClaim));
		}
	}

	[DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
	private static extern int NCryptOpenStorageProvider(out nint provider, string providerName, uint flags);

	[DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
	private static extern int NCryptGetProperty(nint handle, string property, byte[]? output, uint outputBytes, out uint resultBytes, uint flags);

	[DllImport("ncrypt.dll")]
	private static extern int NCryptFreeObject(nint handle);

	[DllImport("crypt32.dll", SetLastError = true)]
	private static extern bool CertCloseStore(nint store, uint flags);

	[DllImport("tbs.dll")]
	private static extern uint Tbsi_GetDeviceInfo(uint size, ref TpmDeviceInfo info);

	private static TbsProbe ProbeTbs()
	{
		try
		{
			uint num = 2150121474u;
			TpmDeviceInfo info = default;
			uint[] array = new uint[3] { 1u, 2u, 0u };
			foreach (uint structVersion in array)
			{
				info = new TpmDeviceInfo
				{
					structVersion = structVersion
				};
				num = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info);
				if (num != 2150121474u)
				{
					break;
				}
			}
			switch (num)
			{
			case 2150121487u:
				return new TbsProbe(false, null);
			default:
				return new TbsProbe(null, null);
			case 0u:
			{
				uint tpmVersion = info.tpmVersion;
				return new TbsProbe(true, tpmVersion switch
				{
					2u => "2.0", 
					1u => "1.2", 
					_ => null, 
				});
			}
			}
		}
		catch
		{
			return new TbsProbe(null, null);
		}
	}

	private static EndorsementKey? ReadEndorsementKey()
	{
		nint provider = IntPtr.Zero;
		try
		{
			if (NCryptOpenStorageProvider(out provider, Settings.PlatformCryptoProviderName, 0u) != 0)
			{
				return null;
			}
			if (NCryptGetProperty(provider, "PCP_EKPUB", null, 0u, out var resultBytes, 0u) != 0 || resultBytes < 8 || resultBytes > 65536)
			{
				return null;
			}
			byte[] array = new byte[resultBytes];
			if (NCryptGetProperty(provider, "PCP_EKPUB", array, resultBytes, out var resultBytes2, 0u) != 0 || resultBytes2 < 8)
			{
				return null;
			}
			return ParseKeyBlob(array.AsSpan(0, (int)resultBytes2));
		}
		catch
		{
			return null;
		}
		finally
		{
			if (provider != IntPtr.Zero)
			{
				try
				{
					NCryptFreeObject(provider);
				}
				catch
				{
				}
			}
		}
	}

	internal static EndorsementKey? ParseKeyBlob(ReadOnlySpan<byte> blob)
	{
		if (blob.Length < 8)
		{
			return null;
		}
		uint num = BitConverter.ToUInt32(blob.Slice(0, 4));
		if (num == 826364754)
		{
			if (blob.Length < 24)
			{
				return null;
			}
			int num2 = (int)BitConverter.ToUInt32(blob.Slice(8, 4));
			int num3 = (int)BitConverter.ToUInt32(blob.Slice(12, 4));
			if (num2 <= 0 || num3 <= 0 || 24 + num2 + num3 > blob.Length)
			{
				return null;
			}
			byte[] exponent = StripLeadingZeros(blob.Slice(24, num2).ToArray());
			byte[] array = StripLeadingZeros(blob.Slice(24 + num2, num3).ToArray());
			if (array.Length == 0)
			{
				return null;
			}
			return new EndorsementKey(HexLower(SHA256.HashData(array)), IsRsa: true, array, exponent, Array.Empty<byte>(), Array.Empty<byte>());
		}
		if (EccPublicMagicKeyBytes.TryGetValue(num, out var value))
		{
			int num4 = (int)BitConverter.ToUInt32(blob.Slice(4, 4));
			if (num4 != value || 8 + 2 * num4 > blob.Length)
			{
				return null;
			}
			byte[] array2 = blob.Slice(8, num4).ToArray();
			byte[] array3 = blob.Slice(8 + num4, num4).ToArray();
			byte[] array4 = new byte[array2.Length + array3.Length];
			array2.CopyTo(array4, 0);
			array3.CopyTo(array4, array2.Length);
			return new EndorsementKey(HexLower(SHA256.HashData(array4)), IsRsa: false, Array.Empty<byte>(), Array.Empty<byte>(), array2, array3);
		}
		return null;
	}

	private static DeviceChipClaim? BuildChipClaim()
	{
		try
		{
			EndorsementKey key = Key;
			if (key == null)
			{
				Log.Auth($"tpm endorsement probe present={Describe(Tbs.Present)} version={Tbs.Version ?? "-"} ek=none cert_source={"none"} provider=- registry=- vendor=- definitive=-", "BuildChipClaim", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\TpmEndorsement.cs", 282);
				return null;
			}
			byte[] array = null;
			string text = "none";
			string value = "-";
			string value2 = "-";
			string value3 = "-";
			string value4 = "-";
			nint provider = IntPtr.Zero;
			try
			{
				if (NCryptOpenStorageProvider(out provider, Settings.PlatformCryptoProviderName, 0u) != 0)
				{
					provider = IntPtr.Zero;
				}
				if (provider != IntPtr.Zero)
				{
					(array, value) = ReadProviderCertificate(provider, key);
					if (array != null)
					{
						text = "provider";
					}
				}
			}
			finally
			{
				if (provider != IntPtr.Zero)
				{
					try
					{
						NCryptFreeObject(provider);
					}
					catch
					{
					}
				}
			}
			if (array == null)
			{
				(array, value2) = ReadRegistryCertificate(key);
				if (array != null)
				{
					text = "registry";
				}
			}
			if (array == null)
			{
				(array, value3, value4) = FindVendorCertificate(key);
				if (array != null)
				{
					text = "vendor_service";
				}
			}
			string text2 = null;
			if (array != null)
			{
				text2 = BoundedBase64(array, Settings.EndorsementCertificateMaxBase64Chars);
				if (text2 == null)
				{
					text = "none";
				}
			}
			Log.Auth($"tpm endorsement probe present={Describe(Tbs.Present)} version={Tbs.Version ?? "-"} ek={(key.IsRsa ? "rsa" : "ecc")} cert_source={text} provider={value} registry={value2} vendor={value3} definitive={value4}", "BuildChipClaim", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\TpmEndorsement.cs", 334);
			return new DeviceChipClaim
			{
				EkSha256 = key.Sha256Hex,
				EkCertificate = text2,
				Source = text
			};
		}
		catch (Exception ex)
		{
			Log.Auth("tpm endorsement probe failed: " + ex.GetType().Name + ": " + ex.Message, "BuildChipClaim", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DeviceIdentity\\TpmEndorsement.cs", 344);
			return null;
		}
	}

	private static (byte[]? Der, string Log) ReadProviderCertificate(nint provider, EndorsementKey key)
	{
		(string, string)[] array = ((!key.IsRsa) ? new (string, string)[4]
		{
			("PCP_ECC_EKCERT", "ecc_ekcert"),
			("PCP_EKCERT", "ekcert"),
			("PCP_ECC_EKNVCERT", "ecc_eknvcert"),
			("PCP_EKNVCERT", "eknvcert")
		} : new (string, string)[4]
		{
			("PCP_RSA_EKCERT", "rsa_ekcert"),
			("PCP_EKCERT", "ekcert"),
			("PCP_RSA_EKNVCERT", "rsa_eknvcert"),
			("PCP_EKNVCERT", "eknvcert")
		});
		List<string> list = new List<string>(array.Length);
		(string, string)[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			(string, string) tuple = array2[i];
			string item = tuple.Item1;
			string item2 = tuple.Item2;
			byte[] array3 = new byte[IntPtr.Size];
			int num;
			uint resultBytes;
			try
			{
				num = NCryptGetProperty(provider, item, array3, (uint)array3.Length, out resultBytes, 0u);
			}
			catch (Exception ex)
			{
				list.Add(item2 + ":" + ex.GetType().Name);
				continue;
			}
			list.Add($"{item2}:0x{(uint)num:X8}");
			if (num != 0 || resultBytes != array3.Length)
			{
				continue;
			}
			nint num2 = (nint)((IntPtr.Size == 8) ? BitConverter.ToInt64(array3, 0) : BitConverter.ToInt32(array3, 0));
			if (num2 == IntPtr.Zero)
			{
				continue;
			}
			try
			{
				using X509Store x509Store = new X509Store(num2);
				foreach (X509Certificate2 certificate in x509Store.Certificates)
				{
					using (certificate)
					{
						if (MatchesKey(certificate.RawData, key))
						{
							return (Der: certificate.RawData, Log: string.Join("/", list));
						}
					}
				}
			}
			catch (Exception ex2)
			{
				List<string> list2 = list;
				int index = list2.Count - 1;
				list2[index] = list2[index] + ":" + ex2.GetType().Name;
			}
			finally
			{
				try
				{
					CertCloseStore(num2, 0u);
				}
				catch
				{
				}
			}
		}
		return (Der: null, Log: string.Join("/", list));
	}

	private static (byte[]? Der, string Log) ReadRegistryCertificate(EndorsementKey key)
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\TPM\\WMI\\Endorsement\\EKCertStore\\Certificates", writable: false);
			if (registryKey == null)
			{
				return (Der: null, Log: "missing");
			}
			string[] valueNames = registryKey.GetValueNames();
			foreach (string name in valueNames)
			{
				if (registryKey.GetValue(name) is byte[] array && array.Length != 0 && MatchesKey(array, key))
				{
					return (Der: array, Log: "found");
				}
			}
			return (Der: null, Log: "none");
		}
		catch (SecurityException)
		{
			return (Der: null, Log: "access_denied");
		}
		catch (UnauthorizedAccessException)
		{
			return (Der: null, Log: "access_denied");
		}
		catch (IOException)
		{
			return (Der: null, Log: "io_error");
		}
		catch (Exception ex4)
		{
			return (Der: null, Log: ex4.GetType().Name);
		}
	}

	private static (byte[]? Der, string Log, string Definitive) FindVendorCertificate(EndorsementKey key)
	{
		string text = Path.Combine(Settings.DeviceAuthDirectory, Settings.EndorsementCertificateCacheFileName);
		string text2 = Path.Combine(Settings.DeviceAuthDirectory, Settings.EndorsementCertificateAttemptMarkerFileName);
		byte[] array = ReadCachedCertificate(text, text2, key);
		if (array != null)
		{
			return (Der: array, Log: "cached", Definitive: "-");
		}
		try
		{
			if (File.Exists(text2))
			{
				int? value = ParseCertificateMarker(File.ReadAllText(text2));
				if (value.HasValue)
				{
					return (Der: null, Log: $"marker:{value}", Definitive: "true");
				}
				File.Delete(text2);
			}
		}
		catch
		{
		}
		(byte[]? Der, int? Status, string Log) tuple = FetchVendorCertificate(key);
		byte[] item = tuple.Der;
		int? item2 = tuple.Status;
		string item3 = tuple.Log;
		bool flag = item2.HasValue && IsVendorAnswerDefinitive(CpuVendor(), item2.Value, CpuBrand());
		if (flag)
		{
			try
			{
				Directory.CreateDirectory(Settings.DeviceAuthDirectory);
				File.WriteAllText(text2, FormatCertificateMarker(item2.Value, DateTimeOffset.UtcNow));
				if (item != null)
				{
					File.WriteAllBytes(text, item);
				}
			}
			catch
			{
			}
		}
		return (Der: item, Log: item3, Definitive: flag ? "true" : "false");
	}

	private static byte[]? ReadCachedCertificate(string cachePath, string markerPath, EndorsementKey key)
	{
		try
		{
			if (!File.Exists(cachePath))
			{
				return null;
			}
			byte[] array = File.ReadAllBytes(cachePath);
			if (MatchesKey(array, key))
			{
				return array;
			}
			File.Delete(cachePath);
			if (File.Exists(markerPath))
			{
				File.Delete(markerPath);
			}
		}
		catch
		{
		}
		return null;
	}

	private static (byte[]? Der, int? Status, string Log) FetchVendorCertificate(EndorsementKey key)
	{
		if (!key.IsRsa)
		{
			return (Der: null, Status: 0, Log: "skipped:ecc");
		}
		string text = CpuVendor();
		string requestUri;
		if (text == "GenuineIntel")
		{
			byte[] array = new byte[key.Modulus.Length + key.Exponent.Length];
			key.Modulus.CopyTo(array, 0);
			key.Exponent.CopyTo(array, key.Modulus.Length);
			string stringToEscape = Convert.ToBase64String(SHA256.HashData(array)).Replace('+', '-').Replace('/', '_');
			requestUri = Settings.IntelEndorsementCertificateServiceUrl + Uri.EscapeDataString(stringToEscape);
		}
		else
		{
			if (!(text == "AuthenticAMD"))
			{
				return (Der: null, Status: 0, Log: "skipped:vendor");
			}
			uint num = ExponentValue(key.Exponent);
			byte[] array2 = new byte[8 + key.Modulus.Length];
			array2[2] = 34;
			array2[3] = 34;
			array2[4] = (byte)(num >> 24);
			array2[5] = (byte)(num >> 16);
			array2[6] = (byte)(num >> 8);
			array2[7] = (byte)num;
			key.Modulus.CopyTo(array2, 8);
			requestUri = Settings.AmdEndorsementCertificateServiceUrl + Convert.ToHexString(SHA256.HashData(array2), 0, 16);
		}
		try
		{
			using HttpClient httpClient = new HttpClient(new HttpClientHandler())
			{
				Timeout = Settings.EndorsementCertificateBudget
			};
			using HttpResponseMessage httpResponseMessage = httpClient.GetAsync(requestUri).GetAwaiter().GetResult();
			int statusCode = (int)httpResponseMessage.StatusCode;
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return (Der: null, Status: statusCode, Log: statusCode.ToString(CultureInfo.InvariantCulture));
			}
			byte[] result = httpResponseMessage.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
			byte[] array3 = ((text == "GenuineIntel") ? DecodeIntelResponse(result) : DecodeCertificateBody(result));
			byte[] array4 = ((array3 != null && MatchesKey(array3, key)) ? array3 : null);
			return (Der: array4, Status: statusCode, Log: (array4 != null) ? $"{statusCode}" : $"{statusCode}:no_match");
		}
		catch (TaskCanceledException)
		{
			return (Der: null, Status: null, Log: "timeout");
		}
		catch (Exception ex2)
		{
			return (Der: null, Status: null, Log: ex2.GetType().Name);
		}
	}

	internal static bool IsVendorAnswerDefinitive(string? cpuVendor, int status, string? cpuBrand)
	{
		switch (status)
		{
		case 0:
			return true;
		case 200:
		case 201:
		case 202:
		case 203:
		case 204:
		case 205:
		case 206:
		case 207:
		case 208:
		case 209:
		case 210:
		case 211:
		case 212:
		case 213:
		case 214:
		case 215:
		case 216:
		case 217:
		case 218:
		case 219:
		case 220:
		case 221:
		case 222:
		case 223:
		case 224:
		case 225:
		case 226:
		case 227:
		case 228:
		case 229:
		case 230:
		case 231:
		case 232:
		case 233:
		case 234:
		case 235:
		case 236:
		case 237:
		case 238:
		case 239:
		case 240:
		case 241:
		case 242:
		case 243:
		case 244:
		case 245:
		case 246:
		case 247:
		case 248:
		case 249:
		case 250:
		case 251:
		case 252:
		case 253:
		case 254:
		case 255:
		case 256:
		case 257:
		case 258:
		case 259:
		case 260:
		case 261:
		case 262:
		case 263:
		case 264:
		case 265:
		case 266:
		case 267:
		case 268:
		case 269:
		case 270:
		case 271:
		case 272:
		case 273:
		case 274:
		case 275:
		case 276:
		case 277:
		case 278:
		case 279:
		case 280:
		case 281:
		case 282:
		case 283:
		case 284:
		case 285:
		case 286:
		case 287:
		case 288:
		case 289:
		case 290:
		case 291:
		case 292:
		case 293:
		case 294:
		case 295:
		case 296:
		case 297:
		case 298:
		case 299:
			return true;
		default:
			if (status != 404)
			{
				return false;
			}
			if (!(cpuVendor != "GenuineIntel"))
			{
				return IsIntelPreOdcaGeneration(cpuBrand);
			}
			return true;
		}
	}

	internal static bool IsIntelPreOdcaGeneration(string? cpuBrand)
	{
		if (string.IsNullOrWhiteSpace(cpuBrand))
		{
			return false;
		}
		Match match = GenerationPrefix.Match(cpuBrand);
		if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			return result <= 10;
		}
		if (cpuBrand.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		Match match2 = CoreModel.Match(cpuBrand);
		if (!match2.Success)
		{
			return false;
		}
		string value = match2.Groups[1].Value;
		if (int.TryParse((value.Length == 5 || value[0] == '1') ? value.Substring(0, 2) : value.Substring(0, 1), NumberStyles.None, CultureInfo.InvariantCulture, out var result2))
		{
			if (result2 >= 2)
			{
				return result2 <= 10;
			}
			return false;
		}
		return false;
	}

	internal static string FormatCertificateMarker(int status, DateTimeOffset at)
	{
		return status.ToString(CultureInfo.InvariantCulture) + " " + at.ToString("O", CultureInfo.InvariantCulture);
	}

	internal static int? ParseCertificateMarker(string? content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return null;
		}
		string text = content.Trim().Split(' ', 2)[0];
		if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || text.Length > 3)
		{
			return null;
		}
		return result;
	}

	internal static string? BoundedBase64(byte[] bytes, int maxChars)
	{
		string text = Convert.ToBase64String(bytes);
		if (text.Length > maxChars)
		{
			return null;
		}
		return text;
	}

	private static byte[]? DecodeIntelResponse(byte[] body)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(body);
			if (!jsonDocument.RootElement.TryGetProperty("certificate", out var value) || value.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			string text = value.GetString();
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			string text2 = text.Trim().Replace('-', '+').Replace('_', '/');
			text2 = text2.PadRight(text2.Length + (4 - text2.Length % 4) % 4, '=');
			return DecodeCertificateBody(Convert.FromBase64String(text2));
		}
		catch
		{
			return null;
		}
	}

	private static byte[]? DecodeCertificateBody(byte[] body)
	{
		try
		{
			if (body.Length > 10 && Encoding.ASCII.GetString(body, 0, 10) == "-----BEGIN")
			{
				using (X509Certificate2 x509Certificate = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(body)))
				{
					return x509Certificate.RawData;
				}
			}
			using X509Certificate2 x509Certificate2 = new X509Certificate2(body);
			return x509Certificate2.RawData;
		}
		catch
		{
			return null;
		}
	}

	internal static bool MatchesKey(byte[] der, EndorsementKey key)
	{
		try
		{
			using X509Certificate2 x509Certificate = new X509Certificate2(der);
			if (key.IsRsa)
			{
				using (RSA rSA = x509Certificate.GetRSAPublicKey())
				{
					byte[] array = rSA?.ExportParameters(includePrivateParameters: false).Modulus;
					return array != null && StripLeadingZeros(array).AsSpan().SequenceEqual(key.Modulus);
				}
			}
			ECPoint? eCPoint = null;
			try
			{
				using ECDsa eCDsa = x509Certificate.GetECDsaPublicKey();
				eCPoint = eCDsa?.ExportParameters(includePrivateParameters: false).Q;
			}
			catch
			{
			}
			if (eCPoint?.X == null)
			{
				using ECDiffieHellman eCDiffieHellman = x509Certificate.GetECDiffieHellmanPublicKey();
				eCPoint = eCDiffieHellman?.ExportParameters(includePrivateParameters: false).Q;
			}
			return eCPoint?.X != null && eCPoint.Value.Y != null && eCPoint.Value.X.AsSpan().SequenceEqual(key.X) && eCPoint.Value.Y.AsSpan().SequenceEqual(key.Y);
		}
		catch
		{
			return false;
		}
	}

	internal static string? CpuVendor()
	{
		try
		{
			if (!X86Base.IsSupported)
			{
				return null;
			}
			(int Eax, int Ebx, int Ecx, int Edx) tuple = X86Base.CpuId(0, 0);
			int item = tuple.Ebx;
			int item2 = tuple.Ecx;
			int item3 = tuple.Edx;
			byte[] array = new byte[12];
			BitConverter.GetBytes(item).CopyTo(array, 0);
			BitConverter.GetBytes(item3).CopyTo(array, 4);
			BitConverter.GetBytes(item2).CopyTo(array, 8);
			return Encoding.ASCII.GetString(array).TrimEnd('\0');
		}
		catch
		{
			return null;
		}
	}

	internal static string? CpuBrand()
	{
		try
		{
			if (!X86Base.IsSupported)
			{
				return null;
			}
			if ((uint)X86Base.CpuId(int.MinValue, 0).Eax < 2147483652u)
			{
				return null;
			}
			byte[] array = new byte[48];
			int num = 0;
			uint[] array2 = new uint[3] { 2147483650u, 2147483651u, 2147483652u };
			for (int i = 0; i < array2.Length; i++)
			{
				(int Eax, int Ebx, int Ecx, int Edx) tuple = X86Base.CpuId((int)array2[i], 0);
				int item = tuple.Eax;
				int item2 = tuple.Ebx;
				int item3 = tuple.Ecx;
				int item4 = tuple.Edx;
				int[] array3 = new int[4] { item, item2, item3, item4 };
				for (int j = 0; j < array3.Length; j++)
				{
					BitConverter.GetBytes(array3[j]).CopyTo(array, num);
					num += 4;
				}
			}
			string text = Encoding.ASCII.GetString(array).Replace("\0", "").Trim();
			return (text.Length == 0) ? null : text;
		}
		catch
		{
			return null;
		}
	}

	private static uint ExponentValue(byte[] exponent)
	{
		uint num = 0u;
		foreach (byte item in exponent.TakeLast(4))
		{
			num = (num << 8) | item;
		}
		if (num != 0)
		{
			return num;
		}
		return 65537u;
	}

	private static byte[] StripLeadingZeros(byte[] bytes)
	{
		int i;
		for (i = 0; i < bytes.Length - 1 && bytes[i] == 0; i++)
		{
		}
		if (i != 0)
		{
			return bytes[i..];
		}
		return bytes;
	}

	private static string HexLower(byte[] bytes)
	{
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	private static string Describe(bool? value)
	{
		if (value.HasValue)
		{
			if (value == true)
			{
				return "true";
			}
			return "false";
		}
		return "unknown";
	}
}
