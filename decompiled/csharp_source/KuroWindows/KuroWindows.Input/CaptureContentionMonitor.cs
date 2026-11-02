using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using ClientTelemetry;
using KuroWindows.Auth;
using KuroWindows.Settings;
using KuroWindows.Sync;

namespace KuroWindows.Input;

public sealed class CaptureContentionMonitor : IDisposable
{
	private sealed record Match(string Kind, string Digest);

	private sealed record Transition(bool Contended, string Kind, string Digest, ContentionEnd Reason, long HeldMs);

	private sealed class FileIdentifiers(long length, DateTime lastWriteUtc, Match[] identifiers)
	{
		public long Length { get; } = length;

		public DateTime LastWriteUtc { get; } = lastWriteUtc;

		public Match[] Identifiers { get; } = identifiers;

		public long CheckedScan { get; set; }
	}

	public const int ScanIntervalMs = 5000;

	private const string KindFileName = "file_name";

	private const string KindProductName = "product_name";

	private const string KindOriginalFilename = "original_filename";

	private const string KindInternalName = "internal_name";

	private const string KindFileDescription = "file_description";

	private const string KindCompanyName = "company_name";

	private const string KindSigner = "signer";

	private const long FileIdleScans = 60L;

	private const uint ProcessQueryLimitedInformation = 4096u;

	private const int ErrorInsufficientBuffer = 122;

	private const int MaxPathChars = 32768;

	private readonly object _sync = new object();

	private readonly object _raiseLock = new object();

	private readonly AutoResetEvent _wake = new AutoResetEvent(initialState: false);

	private readonly ManualResetEventSlim _settled = new ManualResetEventSlim(initialState: true);

	private readonly int _selfPid = Environment.ProcessId;

	private readonly int _selfSessionId = CurrentSessionId();

	private readonly string? _systemRootPrefix = SystemRootPrefix();

	private readonly Dictionary<string, FileIdentifiers> _files = new Dictionary<string, FileIdentifiers>(StringComparer.OrdinalIgnoreCase);

	private char[] _pathBuffer = new char[1024];

	private long _scanCount;

	private bool _scanFailureLogged;

	private HashSet<string> _digests = new HashSet<string>(StringComparer.Ordinal);

	private KuroWindows.Auth.AuthState _authState;

	private bool _recordingArmed;

	private int _generation;

	private bool _matched;

	private string _matchedKind = "";

	private string _matchedDigest = "";

	private bool _published;

	private string _onsetKind = "";

	private string _onsetDigest = "";

	private long _onsetTimestamp;

	private Thread? _thread;

	private volatile bool _disposed;

	public bool IsContended
	{
		get
		{
			lock (_sync)
			{
				return _published;
			}
		}
	}

	public static bool ArmedBySettings
	{
		get
		{
			if (UserSettings.Current.RecordingEnabled)
			{
				return UserSettings.Current.OnboardingCompleted;
			}
			return false;
		}
	}

	private bool ActiveLocked
	{
		get
		{
			bool flag = _digests.Count > 0 && _recordingArmed;
			if (flag)
			{
				KuroWindows.Auth.AuthState authState = _authState;
				bool flag2 = (uint)(authState - 1) <= 2u;
				flag = flag2;
			}
			return flag;
		}
	}

	public (ContentionMatch Match, string HashPrefix)? OnsetMatch
	{
		get
		{
			lock (_sync)
			{
				if (!_published)
				{
					return null;
				}
				ContentionMatch? contentionMatch = WireMatch(_onsetKind);
				return (!contentionMatch.HasValue) ? (((ContentionMatch, string)?)null) : new (ContentionMatch, string)?((contentionMatch.Value, Prefix(_onsetDigest)));
			}
		}
	}

	public event Action<bool>? Changed;

	public event Action<bool>? Scanned;

	public void Start()
	{
		lock (_sync)
		{
			if (_thread == null && !_disposed)
			{
				_thread = new Thread(Run)
				{
					Name = "KuroContention",
					IsBackground = true
				};
				_thread.Start();
			}
		}
	}

	public bool WaitForAnswer(TimeSpan timeout)
	{
		try
		{
			if (!_settled.Wait(timeout))
			{
				KuroLog.LocalOnly("recording", $"capture contention answer not ready within {timeout.TotalMilliseconds:0}ms; using contended={IsContended}", "WaitForAnswer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 150);
			}
		}
		catch (ObjectDisposedException)
		{
		}
		return IsContended;
	}

	public void ApplyPolicy(DesktopMaskingPolicy policy)
	{
		string[] captureContention = policy.Normalize().CaptureContention;
		lock (_raiseLock)
		{
			Transition transition;
			lock (_sync)
			{
				if (_digests.SetEquals(captureContention))
				{
					return;
				}
				bool activeLocked = ActiveLocked;
				_digests = new HashSet<string>(captureContention, StringComparer.Ordinal);
				transition = InputsChangedLocked(activeLocked, listChanged: true);
			}
			KuroLog.LocalOnly("recording", $"capture contention list digests={captureContention.Length}", "ApplyPolicy", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 179);
			Raise(transition);
		}
	}

	public void SetAuthState(KuroWindows.Auth.AuthState state)
	{
		lock (_raiseLock)
		{
			Transition transition;
			lock (_sync)
			{
				if (_authState == state)
				{
					return;
				}
				bool activeLocked = ActiveLocked;
				_authState = state;
				transition = InputsChangedLocked(activeLocked, listChanged: false);
			}
			Raise(transition);
		}
	}

	public void SetRecordingArmed(bool armed)
	{
		lock (_raiseLock)
		{
			Transition transition;
			lock (_sync)
			{
				if (_recordingArmed == armed)
				{
					return;
				}
				bool activeLocked = ActiveLocked;
				_recordingArmed = armed;
				transition = InputsChangedLocked(activeLocked, listChanged: false);
			}
			KuroLog.LocalOnly("recording", "capture contention recording_on=" + (armed ? "true" : "false"), "SetRecordingArmed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 227);
			Raise(transition);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			_wake.Set();
		}
		catch
		{
		}
		try
		{
			_settled.Set();
		}
		catch
		{
		}
	}

	private Transition? InputsChangedLocked(bool wasActive, bool listChanged)
	{
		if (ActiveLocked)
		{
			if (!wasActive)
			{
				_matched = false;
			}
			if (!wasActive | listChanged)
			{
				_generation++;
				_settled.Reset();
				_wake.Set();
			}
		}
		else
		{
			_matched = false;
			_generation++;
			_settled.Set();
		}
		return PublishLocked();
	}

	private Transition? PublishLocked()
	{
		bool flag = _matched && _authState == KuroWindows.Auth.AuthState.SignedIn && ActiveLocked;
		if (flag == _published)
		{
			return null;
		}
		_published = flag;
		if (flag)
		{
			_onsetKind = _matchedKind;
			_onsetDigest = _matchedDigest;
			_onsetTimestamp = Stopwatch.GetTimestamp();
			return new Transition(Contended: true, _matchedKind, _matchedDigest, ContentionEnd.Cleared, 0L);
		}
		ContentionEnd reason = ((_authState != KuroWindows.Auth.AuthState.SignedIn) ? ContentionEnd.SignedOut : ((!_recordingArmed) ? ContentionEnd.RecordingOff : ((_digests.Count == 0) ? ContentionEnd.ListChanged : ContentionEnd.Cleared)));
		long heldMs = (long)Stopwatch.GetElapsedTime(_onsetTimestamp).TotalMilliseconds;
		return new Transition(Contended: false, _onsetKind, _onsetDigest, reason, heldMs);
	}

	private static ContentionMatch? WireMatch(string kind)
	{
		return kind switch
		{
			"file_name" => (ContentionMatch?)ContentionMatch.FileName, 
			"product_name" => ContentionMatch.ProductName, 
			"original_filename" => ContentionMatch.OriginalFilename, 
			"internal_name" => ContentionMatch.InternalName, 
			"file_description" => ContentionMatch.FileDescription, 
			"company_name" => ContentionMatch.CompanyName, 
			"signer" => ContentionMatch.Signer, 
			_ => null, 
		};
	}

	private void Raise(Transition? transition)
	{
		if (transition == null)
		{
			return;
		}
		if (transition.Contended)
		{
			KuroLog.Recording("capture contention on kind=" + transition.Kind + " hash=" + Prefix(transition.Digest), "Raise", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 351);
		}
		else
		{
			string value = transition.Reason switch
			{
				ContentionEnd.RecordingOff => "recording_off", 
				ContentionEnd.SignedOut => "signed_out", 
				ContentionEnd.ListChanged => "list_changed", 
				_ => "cleared", 
			};
			KuroLog.Recording($"capture contention off reason={value} held_ms={transition.HeldMs}", "Raise", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 362);
			Telemetry.Emit(Ev.CaptureContentionEnded(transition.Reason, transition.HeldMs));
		}
		Action<bool> action = Changed;
		if (action == null)
		{
			return;
		}
		Delegate[] invocationList = action.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action<bool> action2 = (Action<bool>)invocationList[i];
			try
			{
				action2(transition.Contended);
			}
			catch (Exception ex)
			{
				KuroLog.Recording("capture contention subscriber failed: " + KuroLog.ExceptionSummary(ex), "Raise", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 380);
			}
		}
	}

	private void Run()
	{
		while (!_disposed)
		{
			int generation;
			HashSet<string> digests;
			bool activeLocked;
			lock (_sync)
			{
				generation = _generation;
				digests = _digests;
				activeLocked = ActiveLocked;
			}
			if (!activeLocked)
			{
				_wake.WaitOne();
				continue;
			}
			bool flag = TryScan(digests, out Match match);
			bool flag2 = false;
			bool published = false;
			lock (_raiseLock)
			{
				Transition transition = null;
				lock (_sync)
				{
					if (generation == _generation && ActiveLocked)
					{
						if (flag)
						{
							_matched = match != null;
							_matchedKind = match?.Kind ?? "";
							_matchedDigest = match?.Digest ?? "";
						}
						_settled.Set();
						transition = PublishLocked();
						flag2 = true;
						published = _published;
					}
				}
				Raise(transition);
			}
			if (flag2)
			{
				RaiseScanned(published);
				_wake.WaitOne(5000);
			}
		}
	}

	private void RaiseScanned(bool published)
	{
		Action<bool> action = Scanned;
		if (action == null)
		{
			return;
		}
		Delegate[] invocationList = action.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action<bool> action2 = (Action<bool>)invocationList[i];
			try
			{
				action2(published);
			}
			catch (Exception ex)
			{
				KuroLog.Recording("capture contention subscriber failed: " + KuroLog.ExceptionSummary(ex), "RaiseScanned", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 459);
			}
		}
	}

	private bool TryScan(HashSet<string> digests, out Match? match)
	{
		match = null;
		long scan = ++_scanCount;
		Process[] processes;
		try
		{
			processes = Process.GetProcesses();
		}
		catch (Exception ex)
		{
			if (!_scanFailureLogged)
			{
				_scanFailureLogged = true;
				KuroLog.Recording("capture contention scan failed: " + KuroLog.ExceptionSummary(ex), "TryScan", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CaptureContentionMonitor.cs", 478);
			}
			return false;
		}
		_scanFailureLogged = false;
		Process[] array = processes;
		foreach (Process process in array)
		{
			try
			{
				if (match == null && process.Id != _selfPid && InThisSession(process))
				{
					match = MatchProcess(process, digests, scan);
				}
			}
			catch
			{
			}
			finally
			{
				process.Dispose();
			}
		}
		PruneFiles(scan);
		return true;
	}

	private Match? MatchProcess(Process process, HashSet<string> digests, long scan)
	{
		string text = QueryImagePath(process.Id);
		if (text == null)
		{
			string processName;
			try
			{
				processName = process.ProcessName;
			}
			catch
			{
				return null;
			}
			string text2 = Digest(processName + ".exe");
			if (text2 == null || !digests.Contains(text2))
			{
				return null;
			}
			return new Match("file_name", text2);
		}
		Match[] array = IdentifiersOf(text, scan);
		foreach (Match match in array)
		{
			if (digests.Contains(match.Digest))
			{
				return match;
			}
		}
		return null;
	}

	private Match[] IdentifiersOf(string path, long scan)
	{
		_files.TryGetValue(path, out FileIdentifiers value);
		if (value != null && value.CheckedScan == scan)
		{
			return value.Identifiers;
		}
		bool flag = IsUnderSystemRoot(path);
		long num = -1L;
		DateTime dateTime = DateTime.MinValue;
		if (!flag)
		{
			try
			{
				FileInfo fileInfo = new FileInfo(path);
				if (fileInfo.Exists)
				{
					num = fileInfo.Length;
					dateTime = fileInfo.LastWriteTimeUtc;
				}
			}
			catch
			{
			}
		}
		if (value != null && value.Length == num && value.LastWriteUtc == dateTime)
		{
			value.CheckedScan = scan;
			return value.Identifiers;
		}
		List<Match> list = new List<Match>(8);
		Add(list, "file_name", SafeFileName(path));
		if (!flag && num >= 0)
		{
			AddVersionResource(list, path);
			Add(list, "signer", SignerName(path));
		}
		FileIdentifiers fileIdentifiers = new FileIdentifiers(num, dateTime, list.ToArray())
		{
			CheckedScan = scan
		};
		_files[path] = fileIdentifiers;
		return fileIdentifiers.Identifiers;
	}

	private void PruneFiles(long scan)
	{
		if (_files.Count == 0)
		{
			return;
		}
		List<string> list = null;
		foreach (var (item, fileIdentifiers2) in _files)
		{
			if (scan - fileIdentifiers2.CheckedScan > 60)
			{
				(list ?? (list = new List<string>())).Add(item);
			}
		}
		if (list == null)
		{
			return;
		}
		foreach (string item2 in list)
		{
			_files.Remove(item2);
		}
	}

	private static void AddVersionResource(List<Match> identifiers, string path)
	{
		try
		{
			FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
			Add(identifiers, "product_name", versionInfo.ProductName);
			Add(identifiers, "original_filename", versionInfo.OriginalFilename);
			Add(identifiers, "internal_name", versionInfo.InternalName);
			Add(identifiers, "file_description", versionInfo.FileDescription);
			Add(identifiers, "company_name", versionInfo.CompanyName);
		}
		catch
		{
		}
	}

	private static string? SignerName(string path)
	{
		try
		{
			using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
			using X509Certificate2 x509Certificate = new X509Certificate2(certificate);
			return x509Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
		}
		catch
		{
			return null;
		}
	}

	private static void Add(List<Match> identifiers, string kind, string? value)
	{
		string text = Digest(value);
		if (text != null)
		{
			identifiers.Add(new Match(kind, text));
		}
	}

	internal static string? Digest(string? value)
	{
		if (value == null)
		{
			return null;
		}
		string text = value.Trim().ToLowerInvariant();
		if (text.Length == 0)
		{
			return null;
		}
		Span<byte> span = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(text), span);
		return Convert.ToHexString(span).ToLowerInvariant();
	}

	private static string Prefix(string digest)
	{
		if (digest.Length < 8)
		{
			return "-";
		}
		return digest.Substring(0, 8);
	}

	private static string? SafeFileName(string path)
	{
		try
		{
			return Path.GetFileName(path);
		}
		catch
		{
			return null;
		}
	}

	private bool InThisSession(Process process)
	{
		if (_selfSessionId < 0)
		{
			return true;
		}
		try
		{
			return process.SessionId == _selfSessionId;
		}
		catch
		{
			return true;
		}
	}

	private static int CurrentSessionId()
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			return process.SessionId;
		}
		catch
		{
			return -1;
		}
	}

	private bool IsUnderSystemRoot(string path)
	{
		if (_systemRootPrefix != null)
		{
			return path.StartsWith(_systemRootPrefix, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static string? SystemRootPrefix()
	{
		try
		{
			string text = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = Environment.GetEnvironmentVariable("SystemRoot");
			}
			return string.IsNullOrWhiteSpace(text) ? null : (text.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
		}
		catch
		{
			return null;
		}
	}

	private string? QueryImagePath(int pid)
	{
		nint num = OpenProcess(4096u, inheritHandle: false, (uint)pid);
		if (num == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			for (int i = 0; i < 2; i++)
			{
				uint size = (uint)_pathBuffer.Length;
				if (QueryFullProcessImageNameW(num, 0u, _pathBuffer, ref size))
				{
					return (size != 0) ? new string(_pathBuffer, 0, (int)size) : null;
				}
				if (Marshal.GetLastWin32Error() != 122 || _pathBuffer.Length >= 32768)
				{
					return null;
				}
				_pathBuffer = new char[32768];
			}
			return null;
		}
		finally
		{
			CloseHandle(num);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] exeName, ref uint size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint handle);
}
