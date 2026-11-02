using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KuroWindows.Settings;

namespace KuroWindows.Auth;

public sealed class ConsentRecorder
{
	public static readonly ConsentRecorder Instance = new ConsentRecorder(() => (!AuthManager.Instance.IsSignedIn) ? null : AuthManager.Instance.Session?.UserId, DefaultWriter, UserSettings.Save);

	public const string LegalVersion = "2026-03-28";

	public static readonly (string Type, string Version, bool AgeConfirmed)[] OnboardingDocuments = new (string, string, bool)[2]
	{
		("tos", "2026-03-28", true),
		("privacy_policy", "2026-03-28", true)
	};

	public static readonly IReadOnlySet<string> AcceptedDocumentTypes = new HashSet<string>(StringComparer.Ordinal) { "tos", "privacy_policy", "consent", "contributor" };

	private readonly Func<string?> _currentUserId;

	private readonly Func<PendingConsent, Task<bool>> _writer;

	private readonly Action _persist;

	private int _flushing;

	public ConsentRecorder(Func<string?> currentUserId, Func<PendingConsent, Task<bool>> writer, Action persist)
	{
		_currentUserId = currentUserId;
		_writer = writer;
		_persist = persist;
	}

	public void Enqueue(string userId, IEnumerable<(string Type, string Version, bool AgeConfirmed)> documents)
	{
		if (!string.IsNullOrEmpty(userId))
		{
			IEnumerable<PendingConsent> entries = documents.Select(((string Type, string Version, bool AgeConfirmed) d) => new PendingConsent(userId, d.Type, d.Version, d.AgeConfirmed));
			UserSettings.Current.AddPendingConsents(entries);
			_persist();
			Task.Run((Func<Task?>)FlushAsync);
		}
	}

	public async Task FlushAsync()
	{
		string userId = _currentUserId();
		if (string.IsNullOrEmpty(userId))
		{
			return;
		}
		PendingConsent[] array = UserSettings.Current.PendingConsents.Where((PendingConsent e) => e.UserId == userId).ToArray();
		if (array.Length == 0 || Interlocked.Exchange(ref _flushing, 1) == 1)
		{
			return;
		}
		try
		{
			PendingConsent[] array2 = array;
			foreach (PendingConsent entry in array2)
			{
				if (!AcceptedDocumentTypes.Contains(entry.DocumentType))
				{
					UserSettings.Current.RemovePendingConsent(entry);
					_persist();
					KuroLog.Auth($"consent dropped doc={entry.DocumentType} version={entry.DocumentVersion}: document type not accepted", "FlushAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\ConsentRecorder.cs", 90);
					continue;
				}
				bool flag;
				try
				{
					flag = await _writer(entry).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (Exception ex)
				{
					KuroLog.Auth("consent write deferred doc=" + entry.DocumentType + ": " + ex.Message, "FlushAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\ConsentRecorder.cs", 98);
					continue;
				}
				if (!flag)
				{
					KuroLog.Auth("consent write deferred doc=" + entry.DocumentType + " version=" + entry.DocumentVersion, "FlushAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\ConsentRecorder.cs", 104);
					continue;
				}
				UserSettings.Current.RemovePendingConsent(entry);
				_persist();
				KuroLog.Auth("consent recorded doc=" + entry.DocumentType + " version=" + entry.DocumentVersion, "FlushAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\ConsentRecorder.cs", 110);
			}
		}
		finally
		{
			Interlocked.Exchange(ref _flushing, 0);
		}
	}

	private static async Task<bool> DefaultWriter(PendingConsent entry)
	{
		AuthManager auth = AuthManager.Instance;
		string accessToken = await auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false);
		APIClient aPIClient = new APIClient();
		aPIClient.SetAccessToken(accessToken);
		return await aPIClient.RecordConsentAsync(entry.DocumentType, entry.DocumentVersion, entry.AgeConfirmed, auth.DeviceId).ConfigureAwait(continueOnCapturedContext: false);
	}
}
