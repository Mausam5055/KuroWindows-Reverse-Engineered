using System;
using System.Text.Json;
using System.Threading;
using ClientTelemetry;

namespace DesktopUpdates;

public sealed class VersionFloor
{
	internal static readonly TimeSpan ClearQuietPeriod = TimeSpan.FromSeconds(30.0);

	private readonly object _sync = new object();

	private readonly UpdateLog _log;

	private readonly string _appVersion;

	private bool _below;

	private long _lastHeardMs;

	private string? _minVersion;

	private int _reported;

	private Action<bool>? _onHeard;

	private Action? _onCleared;

	public bool IsBelowFloor
	{
		get
		{
			lock (_sync)
			{
				return _below;
			}
		}
	}

	public string? MinVersion
	{
		get
		{
			lock (_sync)
			{
				return _minVersion;
			}
		}
	}

	internal VersionFloor(UpdateLog log, string appVersion)
	{
		_log = log;
		_appVersion = appVersion;
	}

	internal void Attach(Action<bool> onHeard, Action onCleared)
	{
		_onHeard = onHeard;
		_onCleared = onCleared;
	}

	public void Heard(string operation, string? responseBody)
	{
		try
		{
			string text = RouteOf(operation);
			string text2 = MinVersionOf(responseBody);
			bool flag;
			lock (_sync)
			{
				flag = !_below;
				_below = true;
				_lastHeardMs = Environment.TickCount64;
				if (text2 != null)
				{
					_minVersion = text2;
				}
			}
			if (Interlocked.Exchange(ref _reported, 1) == 0)
			{
				Telemetry.Emit(Ev.UpdateRequired(text, text2));
			}
			if (flag)
			{
				_log.Update($"version floor: below the minimum route={text} min_version={text2 ?? "-"} app_version={_appVersion}", "Heard", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\VersionFloor.cs", 119);
			}
			Action<bool> onHeard = _onHeard;
			if (onHeard == null)
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(((Action<bool> handler, bool began, UpdateLog _log) state) =>
			{
				var (action, obj, updateLog) = state;
				try
				{
					action(obj);
				}
				catch (Exception ex2)
				{
					updateLog.Update("version floor: handler failed: " + updateLog.ExceptionSummary(ex2), "Heard", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\VersionFloor.cs", 135);
				}
			}, (onHeard, flag, _log), preferLocal: false);
		}
		catch (Exception ex)
		{
			_log.LocalOnly("version floor: hook failed: " + _log.ExceptionSummary(ex), "Heard", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\VersionFloor.cs", 142);
		}
	}

	internal FloorCheck CheckPassed()
	{
		lock (_sync)
		{
			if (!_below)
			{
				return FloorCheck.NotBelow;
			}
			if (Environment.TickCount64 - _lastHeardMs < (long)ClearQuietPeriod.TotalMilliseconds)
			{
				return FloorCheck.Deferred;
			}
			_below = false;
			_minVersion = null;
		}
		_log.Update("version floor: cleared; check-version answered 200 app_version=" + _appVersion, "CheckPassed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\VersionFloor.cs", 165);
		Action onCleared = _onCleared;
		if (onCleared != null)
		{
			ThreadPool.QueueUserWorkItem(((Action handler, UpdateLog _log) state) =>
			{
				var (action, updateLog) = state;
				try
				{
					action();
				}
				catch (Exception ex)
				{
					updateLog.Update("version floor: clear handler failed: " + updateLog.ExceptionSummary(ex), "CheckPassed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\VersionFloor.cs", 178);
				}
			}, (onCleared, _log), preferLocal: false);
		}
		return FloorCheck.Cleared;
	}

	internal static string RouteOf(string operation)
	{
		if (string.IsNullOrWhiteSpace(operation))
		{
			return "unknown";
		}
		int num = operation.IndexOf(':');
		if (num <= 0)
		{
			return operation;
		}
		return operation.Substring(0, num);
	}

	internal static string? MinVersionOf(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return null;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(body);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			JsonElement value = default;
			bool flag = rootElement.TryGetProperty("error", out var value2) && value2.ValueKind == JsonValueKind.Object && value2.TryGetProperty("min_version", out value);
			if (!flag)
			{
				flag = rootElement.TryGetProperty("min_version", out value);
			}
			if (!flag || value.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			string text = value.GetString();
			object result2;
			if (text != null)
			{
				int length = text.Length;
				if (length > 0 && length <= 32 && Version.TryParse(text, out Version _))
				{
					result2 = text;
					goto IL_00be;
				}
			}
			result2 = null;
			goto IL_00be;
			IL_00be:
			return (string?)result2;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
