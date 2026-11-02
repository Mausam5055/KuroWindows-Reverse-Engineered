using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KuroWindows.Sync;

namespace KuroWindows.Input;

public sealed class PrivacyManager : IDisposable
{
	private sealed record Transition(bool Entering, PrivacyContext? StartContext, PrivacyContext? EndContext);

	private static readonly HashSet<string> SelfAppExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KuroWindows.exe" };

	private static readonly HashSet<string> LockShellExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LockApp.exe", "LogonUI.exe" };

	private readonly object _sync = new object();

	private readonly object _dispatchLock = new object();

	private readonly BlockingCollection<Transition> _transitions = new BlockingCollection<Transition>();

	private int _consumerStarted;

	private string _currentExeName = "unknown.exe";

	private string _currentAppName = "Unknown";

	private string _currentWindowTitle = "";

	private string? _currentBrowserUrl;

	private int _currentDisplayIndex;

	private bool _isSecureFieldFocused;

	private bool _isCameraActive;

	private bool _isCaptureContended;

	private bool _inPrivacy;

	private PrivacyContext? _currentContext;

	private DesktopMaskingPolicy _policy = new DesktopMaskingPolicy();

	public Action<PrivacyContext>? OnPrivacyStart;

	public Action<PrivacyContext>? OnPrivacyEnd;

	public bool LastKnownExeIsLockShell
	{
		get
		{
			lock (_sync)
			{
				return LockShellExes.Contains(_currentExeName);
			}
		}
	}

	public PrivacyContext? Prime(ForegroundWindowInfo info, bool isSecureFieldFocused, bool isCameraActive = false, bool isCaptureContended = false)
	{
		lock (_sync)
		{
			_currentExeName = info.ExeName;
			_currentAppName = info.AppName;
			_currentWindowTitle = info.WindowTitle;
			_currentBrowserUrl = info.BrowserUrl;
			_currentDisplayIndex = info.DisplayIndex;
			_isSecureFieldFocused = isSecureFieldFocused;
			_isCameraActive = isCameraActive;
			_isCaptureContended = isCaptureContended;
			PrivacyContext privacyContext = MatchSensitiveContextLocked(includeLockShell: true);
			_inPrivacy = privacyContext != null;
			_currentContext = privacyContext;
			return privacyContext;
		}
	}

	public PrivacyContext? PrimeCaptureContention(bool isCaptureContended)
	{
		lock (_sync)
		{
			_isCaptureContended = isCaptureContended;
			if (!isCaptureContended)
			{
				return null;
			}
			PrivacyContext privacyContext = CaptureContentionContextLocked();
			_inPrivacy = true;
			_currentContext = privacyContext;
			return privacyContext;
		}
	}

	public void CameraActiveChanged(bool isActive)
	{
		lock (_dispatchLock)
		{
			Transition transition;
			lock (_sync)
			{
				_isCameraActive = isActive;
				transition = EvaluateLocked();
			}
			FireTransition(transition);
		}
	}

	public void CaptureContentionChanged(bool isContended)
	{
		lock (_dispatchLock)
		{
			Transition transition;
			lock (_sync)
			{
				_isCaptureContended = isContended;
				transition = EvaluateLocked();
			}
			FireTransition(transition);
		}
	}

	public void UpdateForeground(ForegroundWindowInfo info)
	{
		lock (_dispatchLock)
		{
			Transition transition;
			lock (_sync)
			{
				_currentExeName = info.ExeName;
				_currentAppName = info.AppName;
				_currentWindowTitle = info.WindowTitle;
				_currentBrowserUrl = info.BrowserUrl;
				_currentDisplayIndex = info.DisplayIndex;
				_isSecureFieldFocused = false;
				transition = EvaluateLocked();
			}
			FireTransition(transition);
		}
	}

	public void TitleDidChange(ForegroundWindowInfo info)
	{
		lock (_dispatchLock)
		{
			Transition transition;
			lock (_sync)
			{
				_currentExeName = info.ExeName;
				_currentAppName = info.AppName;
				_currentWindowTitle = info.WindowTitle;
				_currentBrowserUrl = info.BrowserUrl;
				_currentDisplayIndex = info.DisplayIndex;
				transition = EvaluateLocked();
			}
			FireTransition(transition);
		}
	}

	public void SecureFieldChanged(bool isSecure)
	{
		lock (_dispatchLock)
		{
			Transition transition;
			lock (_sync)
			{
				_isSecureFieldFocused = isSecure;
				transition = EvaluateLocked();
			}
			FireTransition(transition);
		}
	}

	public void ApplyPolicy(DesktopMaskingPolicy policy)
	{
		lock (_dispatchLock)
		{
			Transition transition;
			lock (_sync)
			{
				_policy = policy.Normalize();
				transition = EvaluateLocked();
			}
			FireTransition(transition);
		}
	}

	private Transition? EvaluateLocked()
	{
		PrivacyContext privacyContext = MatchSensitiveContextLocked(includeLockShell: true);
		bool flag = privacyContext != null;
		if (flag && !_inPrivacy)
		{
			_inPrivacy = true;
			_currentContext = privacyContext;
			return new Transition(Entering: true, privacyContext, null);
		}
		if (!flag && _inPrivacy)
		{
			_inPrivacy = false;
			PrivacyContext endContext = _currentContext ?? new PrivacyContext("unknown", null, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
			_currentContext = null;
			return new Transition(Entering: false, null, endContext);
		}
		if (flag)
		{
			_currentContext = privacyContext;
		}
		return null;
	}

	private PrivacyContext? MatchSensitiveContextLocked(bool includeLockShell = false)
	{
		if (_isCaptureContended)
		{
			return CaptureContentionContextLocked();
		}
		if (includeLockShell && LockShellExes.Contains(_currentExeName))
		{
			return new PrivacyContext("os_lock_screen", _currentAppName, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
		}
		if (_policy.MaskSelfApp && SelfAppExes.Contains(_currentExeName))
		{
			return new PrivacyContext("self_app", _currentAppName, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
		}
		bool flag = _policy.AllowedApps.Any((string app) => MatchesPolicyApp(app, _currentExeName, _currentAppName));
		if (!flag)
		{
			string text = _policy.BlockedApps.FirstOrDefault((string app) => MatchesPolicyApp(app, _currentExeName, _currentAppName));
			if (!string.IsNullOrWhiteSpace(text))
			{
				return new PrivacyContext("policy_blocked_app", text, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
			}
		}
		if (_policy.MaskPasswordFields && _isSecureFieldFocused)
		{
			return new PrivacyContext("password_field", null, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
		}
		bool flag2 = BrowserCatalog.IsBrowserExe(_currentExeName);
		if ((!flag & flag2) && !string.IsNullOrWhiteSpace(_currentWindowTitle))
		{
			string pageTitle = BrowserCatalog.CleanPageTitle(_currentWindowTitle);
			string text2 = _policy.SensitiveBrowserTitleKeywords.FirstOrDefault((string keyword) => !string.IsNullOrWhiteSpace(keyword) && pageTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
			if (text2 != null)
			{
				return new PrivacyContext("policy_sensitive_browser_title_keyword", text2, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
			}
			string text3 = _policy.ExcludedBrowserTitleKeywords.FirstOrDefault((string keyword) => !string.IsNullOrWhiteSpace(keyword) && pageTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
			if (text3 != null)
			{
				return new PrivacyContext("policy_excluded_browser_title_keyword", text3, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
			}
		}
		if (_policy.MaskCamera && _isCameraActive)
		{
			return new PrivacyContext("camera", null, _currentExeName, _currentWindowTitle, _currentAppName, _currentDisplayIndex, _currentBrowserUrl);
		}
		return null;
	}

	private PrivacyContext CaptureContentionContextLocked()
	{
		return new PrivacyContext("capture_contention", null, "", "", "", _currentDisplayIndex, null);
	}

	private static bool MatchesPolicyApp(string policyApp, string exeName, string appName)
	{
		if (string.IsNullOrWhiteSpace(policyApp))
		{
			return false;
		}
		if (!string.Equals(policyApp, exeName, StringComparison.OrdinalIgnoreCase) && !string.Equals(policyApp, appName, StringComparison.OrdinalIgnoreCase) && !exeName.Contains(policyApp, StringComparison.OrdinalIgnoreCase))
		{
			return appName.Contains(policyApp, StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private void FireTransition(Transition? transition)
	{
		if (transition == null)
		{
			return;
		}
		if (Interlocked.Exchange(ref _consumerStarted, 1) == 0)
		{
			Task.Factory.StartNew(DispatchTransitions, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}
		try
		{
			_transitions.Add(transition);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void DispatchTransitions()
	{
		foreach (Transition item in _transitions.GetConsumingEnumerable())
		{
			if (item.Entering && item.StartContext != null)
			{
				OnPrivacyStart?.Invoke(item.StartContext);
			}
			else if (!item.Entering && item.EndContext != null)
			{
				OnPrivacyEnd?.Invoke(item.EndContext);
			}
		}
	}

	public void Dispose()
	{
		_transitions.CompleteAdding();
	}
}
