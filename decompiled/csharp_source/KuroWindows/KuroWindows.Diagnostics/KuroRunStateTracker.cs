using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace KuroWindows.Diagnostics;

internal static class KuroRunStateTracker
{
	private sealed record RunState
	{
		[JsonPropertyName("run_id")]
		public string? RunId { get; init; }

		[JsonPropertyName("started_at")]
		public DateTimeOffset StartedAt { get; init; }

		[JsonPropertyName("last_seen_at")]
		public DateTimeOffset LastSeenAt { get; init; }

		[JsonPropertyName("app_version")]
		public string? AppVersion { get; init; }

		[JsonPropertyName("platform")]
		public string? Platform { get; init; }

		[JsonPropertyName("process_id")]
		public int ProcessId { get; init; }

		[JsonPropertyName("launch_args")]
		public string? LaunchArgs { get; init; }

		[JsonPropertyName("milestone")]
		public string? Milestone { get; init; }

		[JsonPropertyName("exit_reason")]
		public string? ExitReason { get; init; }

		[JsonPropertyName("last_exception_type")]
		public string? LastExceptionType { get; init; }

		[JsonPropertyName("last_exception_message")]
		public string? LastExceptionMessage { get; init; }

		[JsonPropertyName("last_exception_detail")]
		public string? LastExceptionDetail { get; init; }

		[JsonPropertyName("last_error_source")]
		public string? LastErrorSource { get; init; }

		[JsonPropertyName("last_error_type")]
		public string? LastErrorType { get; init; }

		[JsonPropertyName("last_error_message")]
		public string? LastErrorMessage { get; init; }

		[JsonPropertyName("clean_shutdown")]
		public bool? CleanShutdown { get; init; }
	}

	private const int MaxMessageChars = 240;

	private const int MaxDetailChars = 2048;

	private static readonly object Sync = new object();

	private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "diagnostics");

	private static readonly string StatePath = Path.Combine(StateDir, "last-run.json");

	private static RunState? _current;

	private static int _writeFailureLogged;

	private static int _suppressed;

	private static Timer? _heartbeat;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true
	};

	private static bool IsSuppressed => Volatile.Read(in _suppressed) != 0;

	public static void Suppress()
	{
		Volatile.Write(ref _suppressed, 1);
	}

	public static void MarkStarted(string[] args)
	{
		Update((RunState? _) => Fresh(DateTimeOffset.UtcNow) with
		{
			LaunchArgs = SummarizeArgs(args),
			Milestone = "program_main"
		});
	}

	public static void MarkMilestone(string milestone)
	{
		Update((RunState? current) => (!(current == null)) ? current with
		{
			LastSeenAt = DateTimeOffset.UtcNow,
			Milestone = milestone
		} : null);
	}

	public static void MarkFatal(string exitReason, Exception ex)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Update((RunState? current) =>
		{
			RunState? obj = current ?? Fresh(now);
			return obj with
			{
				LastSeenAt = now,
				ExitReason = exitReason,
				LastExceptionType = ex.GetType().FullName,
				LastExceptionMessage = Trim(ex.Message, 240),
				LastExceptionDetail = Trim(ex.ToString(), 2048),
				CleanShutdown = false
			};
		});
	}

	public static void MarkError(string source, Exception ex)
	{
		Update((RunState? current) => (!(current == null)) ? current with
		{
			LastSeenAt = DateTimeOffset.UtcNow,
			LastErrorSource = source,
			LastErrorType = ex.GetType().FullName,
			LastErrorMessage = Trim(ex.Message, 240)
		} : null);
	}

	public static void MarkExitAborted(string source, Exception ex)
	{
		Update((RunState? current) => (!(current == null)) ? current with
		{
			LastSeenAt = DateTimeOffset.UtcNow,
			Milestone = source,
			ExitReason = null,
			CleanShutdown = false,
			LastErrorSource = source,
			LastErrorType = ex.GetType().FullName,
			LastErrorMessage = Trim(ex.Message, 240)
		} : null);
	}

	public static void StartHeartbeat()
	{
		if (IsSuppressed || _heartbeat != null)
		{
			return;
		}
		_heartbeat = new Timer((object? _) =>
		{
			Update((RunState? current) => (!(current == null)) ? current with
			{
				LastSeenAt = DateTimeOffset.UtcNow
			} : null);
		}, null, TimeSpan.FromMinutes(5.0), TimeSpan.FromMinutes(5.0));
	}

	public static void MarkCleanShutdown(string exitReason)
	{
		Update((RunState? current) =>
		{
			RunState runState = current ?? ReadState();
			return ((object)runState == null) ? null : runState with
			{
				LastSeenAt = DateTimeOffset.UtcNow,
				Milestone = "shutdown_complete",
				ExitReason = exitReason,
				CleanShutdown = true
			};
		});
	}

	private static void Update(Func<RunState?, RunState?> change)
	{
		if (IsSuppressed)
		{
			return;
		}
		try
		{
			lock (Sync)
			{
				RunState runState = change(_current);
				if (!(runState == null))
				{
					_current = runState;
					WriteStateLocked(runState);
				}
			}
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _writeFailureLogged, 1) == 0)
			{
				KuroLog.LocalOnly("crash", "run-state write failed: " + ex.Message, "Update", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\KuroRunStateTracker.cs", 170);
			}
		}
	}

	private static RunState Fresh(DateTimeOffset now)
	{
		return new RunState
		{
			RunId = Guid.NewGuid().ToString("N"),
			StartedAt = now,
			LastSeenAt = now,
			AppVersion = AppConfig.AppVersion,
			Platform = "windows",
			ProcessId = Environment.ProcessId,
			CleanShutdown = false
		};
	}

	private static RunState? ReadState()
	{
		try
		{
			if (!File.Exists(StatePath))
			{
				return null;
			}
			using FileStream utf8Json = File.OpenRead(StatePath);
			return JsonSerializer.Deserialize<RunState>(utf8Json, JsonOptions);
		}
		catch
		{
			return null;
		}
	}

	private static void WriteStateLocked(RunState state)
	{
		Directory.CreateDirectory(StateDir);
		string text = $"{StatePath}.{Environment.ProcessId}.tmp";
		File.WriteAllText(text, JsonSerializer.Serialize(state, JsonOptions));
		File.Move(text, StatePath, overwrite: true);
	}

	private static string SummarizeArgs(string[] args)
	{
		if (args.Length == 0)
		{
			return "-";
		}
		IEnumerable<string> values = args.Select((string arg) =>
		{
			if (arg.StartsWith("com.kuro.app://", StringComparison.OrdinalIgnoreCase) || arg.Contains("/auth/callback", StringComparison.OrdinalIgnoreCase))
			{
				return "[auth-callback]";
			}
			return (arg.Length > 80) ? (arg.Substring(0, 80) + "...") : arg;
		}).Take(8);
		return string.Join(" ", values);
	}

	private static string Trim(string value, int max)
	{
		string text = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " | ")
			.Trim();
		if (text.Length > max)
		{
			return text.Substring(0, max) + "...";
		}
		return text;
	}
}
