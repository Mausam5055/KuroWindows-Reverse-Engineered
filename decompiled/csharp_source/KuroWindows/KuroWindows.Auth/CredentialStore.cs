using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KuroWindows.Auth;

internal static class CredentialStore
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct CREDENTIAL
	{
		public uint Flags;

		public uint Type;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string TargetName;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? Comment;

		public long LastWritten;

		public uint CredentialBlobSize;

		public nint CredentialBlob;

		public uint Persist;

		public uint AttributeCount;

		public nint Attributes;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? TargetAlias;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? UserName;
	}

	private struct DATA_BLOB
	{
		public int CbData;

		public nint PbData;
	}

	private const uint CRED_TYPE_GENERIC = 1u;

	private const uint CRED_PERSIST_LOCAL_MACHINE = 2u;

	private const uint CRYPTPROTECT_UI_FORBIDDEN = 1u;

	private static readonly string FallbackPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "auth", "session.dpapi");

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredReadW(string target, uint type, uint flags, out nint credentialPtr);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredDeleteW(string target, uint type, uint flags);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern void CredFree(nint buffer);

	[DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, nint pOptionalEntropy, nint pvReserved, nint pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

	[DllImport("crypt32.dll", SetLastError = true)]
	private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, nint ppszDataDescr, nint pOptionalEntropy, nint pvReserved, nint pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint LocalFree(nint hMem);

	public static void Save(string target, string userName, string secret)
	{
		try
		{
			SaveToCredentialManager(target, userName, secret);
			DeleteFallback();
		}
		catch (Exception ex)
		{
			try
			{
				SaveFallback(secret);
				DeleteCredential(target);
				Log("Credential Manager save failed; stored auth session with DPAPI file fallback: " + ex.Message, "Save", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\CredentialStore.cs", 92);
			}
			catch (Exception ex2)
			{
				throw new InvalidOperationException("Unable to securely persist the auth session.", new AggregateException(ex, ex2));
			}
		}
	}

	public static string? Load(string target)
	{
		string text = null;
		try
		{
			text = LoadFromCredentialManager(target);
		}
		catch (Exception ex)
		{
			Log("Credential Manager load failed; trying DPAPI file fallback: " + ex.Message, "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\CredentialStore.cs", 112);
		}
		if (text != null)
		{
			return text;
		}
		try
		{
			return LoadFallback();
		}
		catch (Exception ex2)
		{
			Log("DPAPI auth session fallback load failed: " + ex2.Message, "Load", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\CredentialStore.cs", 124);
			DeleteFallback();
			return null;
		}
	}

	public static void Delete(string target)
	{
		DeleteCredential(target);
		DeleteFallback();
	}

	private static void SaveToCredentialManager(string target, string userName, string secret)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(secret);
		nint num = Marshal.AllocHGlobal(bytes.Length);
		try
		{
			Marshal.Copy(bytes, 0, num, bytes.Length);
			CREDENTIAL credential = new CREDENTIAL
			{
				Type = 1u,
				TargetName = target,
				UserName = userName,
				CredentialBlobSize = (uint)bytes.Length,
				CredentialBlob = num,
				Persist = 2u
			};
			if (!CredWriteW(ref credential, 0u))
			{
				int lastWin32Error = Marshal.GetLastWin32Error();
				throw new InvalidOperationException($"CredWriteW failed for '{target}' (Win32 error {lastWin32Error}, blob {bytes.Length} bytes)");
			}
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
	}

	private static string? LoadFromCredentialManager(string target)
	{
		if (!CredReadW(target, 1u, 0u, out var credentialPtr) || credentialPtr == IntPtr.Zero)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			if (lastWin32Error != 1168)
			{
				Log($"Credential Manager read failed (Win32 error {lastWin32Error}); trying DPAPI file fallback", "LoadFromCredentialManager", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\CredentialStore.cs", 171);
			}
			return null;
		}
		try
		{
			CREDENTIAL cREDENTIAL = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
			if (cREDENTIAL.CredentialBlobSize == 0 || cREDENTIAL.CredentialBlob == IntPtr.Zero)
			{
				return null;
			}
			byte[] array = new byte[cREDENTIAL.CredentialBlobSize];
			Marshal.Copy(cREDENTIAL.CredentialBlob, array, 0, array.Length);
			return Encoding.UTF8.GetString(array);
		}
		finally
		{
			CredFree(credentialPtr);
		}
	}

	private static void DeleteCredential(string target)
	{
		if (!CredDeleteW(target, 1u, 0u))
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			if (lastWin32Error != 1168)
			{
				Log($"Credential Manager delete failed (Win32 error {lastWin32Error})", "DeleteCredential", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\CredentialStore.cs", 197);
			}
		}
	}

	private static void SaveFallback(string secret)
	{
		byte[] bytes = Protect(Encoding.UTF8.GetBytes(secret));
		Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath));
		File.WriteAllBytes(FallbackPath, bytes);
	}

	private static string? LoadFallback()
	{
		if (!File.Exists(FallbackPath))
		{
			return null;
		}
		byte[] bytes = Unprotect(File.ReadAllBytes(FallbackPath));
		return Encoding.UTF8.GetString(bytes);
	}

	private static void DeleteFallback()
	{
		try
		{
			if (File.Exists(FallbackPath))
			{
				File.Delete(FallbackPath);
			}
		}
		catch (Exception ex)
		{
			Log("DPAPI auth session fallback delete failed: " + ex.Message, "DeleteFallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\CredentialStore.cs", 228);
		}
	}

	private static byte[] Protect(byte[] bytes)
	{
		return TransformWithDpapi(bytes, protect: true);
	}

	private static byte[] Unprotect(byte[] bytes)
	{
		return TransformWithDpapi(bytes, protect: false);
	}

	private static byte[] TransformWithDpapi(byte[] bytes, bool protect)
	{
		if (bytes.Length == 0)
		{
			return Array.Empty<byte>();
		}
		nint num = Marshal.AllocHGlobal(bytes.Length);
		DATA_BLOB pDataOut = default;
		try
		{
			Marshal.Copy(bytes, 0, num, bytes.Length);
			DATA_BLOB pDataIn = new DATA_BLOB
			{
				CbData = bytes.Length,
				PbData = num
			};
			if (!(protect ? CryptProtectData(ref pDataIn, AppConfig.LocalAppName + " auth session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1u, out pDataOut) : CryptUnprotectData(ref pDataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1u, out pDataOut)))
			{
				int lastWin32Error = Marshal.GetLastWin32Error();
				string value = (protect ? "CryptProtectData" : "CryptUnprotectData");
				throw new InvalidOperationException($"{value} failed (Win32 error {lastWin32Error})");
			}
			byte[] array = new byte[pDataOut.CbData];
			Marshal.Copy(pDataOut.PbData, array, 0, array.Length);
			return array;
		}
		finally
		{
			Marshal.FreeHGlobal(num);
			if (pDataOut.PbData != IntPtr.Zero)
			{
				LocalFree(pDataOut.PbData);
			}
		}
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Auth(message, memberName, filePath, lineNumber);
	}
}
