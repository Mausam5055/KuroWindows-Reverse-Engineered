using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClientTelemetry;
using NuGet.Versioning;

namespace DesktopUpdates;

internal sealed class UpdateRecord
{
	internal sealed record Entry
	{
		[JsonPropertyName("to_version")]
		public string ToVersion { get; init; } = "";

		[JsonPropertyName("from_version")]
		public string? FromVersion { get; init; }

		[JsonPropertyName("staged_at")]
		public DateTimeOffset? StagedAt { get; init; }

		[JsonPropertyName("staged_trigger")]
		public string? StagedTrigger { get; init; }

		[JsonPropertyName("handed_at")]
		public DateTimeOffset? HandedAt { get; init; }

		[JsonPropertyName("apply_trigger")]
		public string? ApplyTrigger { get; init; }

		[JsonPropertyName("handoffs")]
		public int Handoffs { get; init; }

		[JsonPropertyName("reported_handoffs")]
		public int ReportedHandoffs { get; init; }
	}

	private const string NotApplied = "not_applied";

	private static readonly TimeSpan QuitApplyWindow = TimeSpan.FromMinutes(2.0);

	private const string FileName = "update-state.json";

	private static readonly object Sync = new object();

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true
	};

	private readonly string _filePath;

	private readonly string _appVersion;

	private readonly UpdateLog _log;

	internal UpdateRecord(string localDataFolder, string appVersion, UpdateLog log)
	{
		_filePath = Path.Combine(localDataFolder, "update-state.json");
		_appVersion = appVersion;
		_log = log;
	}

	public void NoteStaged(string version, UpdateTrigger trigger, bool landedNow)
	{
		Change((Entry? entry) =>
		{
			Entry result = ((entry != null && entry.ToVersion == version) ? ((!(!entry.StagedAt.HasValue & landedNow)) ? null : entry with
			{
				StagedAt = DateTimeOffset.UtcNow,
				StagedTrigger = trigger.ToWire()
			}) : new Entry
			{
				ToVersion = version,
				FromVersion = _appVersion,
				StagedAt = (landedNow ? new DateTimeOffset?(DateTimeOffset.UtcNow) : ((DateTimeOffset?)null)),
				StagedTrigger = trigger.ToWire()
			});
			return result;
		});
	}

	public void NoteHandoff(string version, UpdateTrigger trigger)
	{
		Change((Entry? entry) =>
		{
			Entry entry2 = ((entry != null && entry.ToVersion == version) ? entry : new Entry
			{
				ToVersion = version,
				FromVersion = _appVersion
			});
			return entry2 with
			{
				HandedAt = DateTimeOffset.UtcNow,
				ApplyTrigger = trigger.ToWire(),
				Handoffs = entry2.Handoffs + 1
			};
		});
	}

	public TimeSpan ApplyBackoffRemaining(string version, bool urgent)
	{
		Entry entry = Read();
		if (!(entry == null) && !(entry.ToVersion != version) && entry.Handoffs > 0)
		{
			DateTimeOffset? handedAt = entry.HandedAt;
			if (handedAt.HasValue)
			{
				DateTimeOffset valueOrDefault = handedAt.GetValueOrDefault();
				TimeSpan timeSpan = valueOrDefault + UpdateService.HandoffRetryStep(entry.Handoffs, urgent) - DateTimeOffset.UtcNow;
				if (!(timeSpan > TimeSpan.Zero))
				{
					return TimeSpan.Zero;
				}
				return timeSpan;
			}
		}
		return TimeSpan.Zero;
	}

	public void ReportLaunch(string? priorVersion, string? priorExitReason)
	{
		try
		{
			string appVersion = _appVersion;
			Entry entry = Read();
			bool flag;
			long? num;
			if (priorVersion != null && priorVersion.Length > 0 && priorVersion != appVersion)
			{
				flag = entry != null && SameRelease(entry.ToVersion, appVersion);
				if (flag)
				{
					DateTimeOffset? stagedAt = entry.StagedAt;
					if (stagedAt.HasValue)
					{
						DateTimeOffset valueOrDefault = stagedAt.GetValueOrDefault();
						stagedAt = entry.HandedAt;
						if (stagedAt.HasValue)
						{
							DateTimeOffset valueOrDefault2 = stagedAt.GetValueOrDefault();
							if (valueOrDefault2 >= valueOrDefault)
							{
								num = (long)(valueOrDefault2 - valueOrDefault).TotalSeconds;
								goto IL_00cc;
							}
						}
					}
				}
				num = null;
				goto IL_00cc;
			}
			if (entry == null)
			{
				return;
			}
			if (!IsNewer(entry.ToVersion, appVersion) || SameRelease(entry.ToVersion, appVersion))
			{
				Delete();
				return;
			}
			if (entry.ApplyTrigger == UpdateTrigger.Quit.ToWire())
			{
				DateTimeOffset? stagedAt = entry.HandedAt;
				if (stagedAt.HasValue)
				{
					DateTimeOffset valueOrDefault3 = stagedAt.GetValueOrDefault();
					if (DateTimeOffset.UtcNow - valueOrDefault3 < QuitApplyWindow)
					{
						return;
					}
				}
			}
			if (entry.Handoffs > entry.ReportedHandoffs && (priorExitReason == "update_restart" || entry.ApplyTrigger == UpdateTrigger.Quit.ToWire()))
			{
				Count.Upgrade(UpdateOutcome.Fail, 1L);
				string msg = $"update not applied version={entry.ToVersion} running={appVersion} trigger={entry.ApplyTrigger ?? "-"} handoffs={entry.Handoffs}";
				if (UpdateService.ReportsAttempt(entry.Handoffs))
				{
					Telemetry.Emit(Ev.UpdateFailed(UpdateStage.Apply, "not_applied", "Update.exe did not install v" + entry.ToVersion + "; this run is still v" + appVersion, TriggerOf(entry.ApplyTrigger), entry.Handoffs));
					_log.Update(msg, "ReportLaunch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 183);
				}
				else
				{
					_log.LocalOnly(msg, "ReportLaunch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 187);
				}
				Change((Entry? e) => (!(e != null) || !(e.ToVersion == entry.ToVersion)) ? null : e with
				{
					ReportedHandoffs = e.Handoffs
				});
			}
			return;
			IL_00cc:
			long? num2 = num;
			Count.Upgrade(UpdateOutcome.Ok, 1L);
			Telemetry.Emit(Ev.UpdateApplied(appVersion, priorVersion, flag ? TriggerOf(entry.ApplyTrigger) : ((UpdateTrigger?)null), num2));
			_log.Update($"update applied from={priorVersion} to={appVersion} trigger={(flag ? (entry.ApplyTrigger ?? "-") : "-")} waited_s={num2?.ToString() ?? "-"}", "ReportLaunch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 140);
			Delete();
		}
		catch (Exception ex)
		{
			_log.LocalOnly("update record launch report failed: " + _log.ExceptionSummary(ex), "ReportLaunch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 193);
		}
	}

	internal Entry? Read()
	{
		lock (Sync)
		{
			try
			{
				string filePath = _filePath;
				if (!File.Exists(filePath))
				{
					return null;
				}
				Entry entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(filePath), JsonOptions);
				object result;
				if ((object)entry != null)
				{
					string toVersion = entry.ToVersion;
					if (toVersion != null && toVersion.Length > 0)
					{
						result = entry;
						goto IL_0052;
					}
				}
				result = null;
				goto IL_0052;
				IL_0052:
				return (Entry?)result;
			}
			catch (Exception ex)
			{
				_log.LocalOnly("update record read failed: " + _log.ExceptionSummary(ex), "Read", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 214);
				return null;
			}
		}
	}

	private void Change(Func<Entry?, Entry?> change)
	{
		lock (Sync)
		{
			try
			{
				Entry entry = change(Read());
				if (!(entry == null))
				{
					string filePath = _filePath;
					Directory.CreateDirectory(Path.GetDirectoryName(filePath));
					string text = filePath + ".tmp";
					File.WriteAllText(text, JsonSerializer.Serialize(entry, JsonOptions));
					File.Move(text, filePath, overwrite: true);
				}
			}
			catch (Exception ex)
			{
				_log.LocalOnly("update record write failed: " + _log.ExceptionSummary(ex), "Change", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 241);
			}
		}
	}

	private void Delete()
	{
		lock (Sync)
		{
			try
			{
				File.Delete(_filePath);
			}
			catch (Exception ex)
			{
				_log.LocalOnly("update record delete failed: " + _log.ExceptionSummary(ex), "Delete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateRecord.cs", 256);
			}
		}
	}

	internal static bool IsNewer(string candidate, string current)
	{
		if (SemanticVersion.TryParse(candidate, out SemanticVersion version) && SemanticVersion.TryParse(current, out SemanticVersion version2))
		{
			return version > version2;
		}
		return false;
	}

	internal static bool SameRelease(string feedVersion, string appVersion)
	{
		if (SemanticVersion.TryParse(feedVersion, out SemanticVersion version) && SemanticVersion.TryParse(appVersion, out SemanticVersion version2) && version.Major == version2.Major && version.Minor == version2.Minor)
		{
			return version.Patch == version2.Patch;
		}
		return false;
	}

	private static UpdateTrigger? TriggerOf(string? wire)
	{
		return wire switch
		{
			"heartbeat" => (UpdateTrigger?)UpdateTrigger.Heartbeat, 
			"required" => UpdateTrigger.Required, 
			"user" => UpdateTrigger.User, 
			"launch" => UpdateTrigger.Launch, 
			"quit" => UpdateTrigger.Quit, 
			_ => null, 
		};
	}
}
