using System;
using ClientTelemetry;

namespace KuroWindows;

internal sealed class SessionParks
{
	internal static readonly TimeSpan ReportThreshold = TimeSpan.FromMinutes(1.0);

	private readonly object _sync = new object();

	private long _startedTicks;

	private bool _clipOpened;

	private string _recordingId = "-";

	private string _reason = "";

	private string _heldBy = "";

	private bool _atStart;

	private long _refusals;

	private ResumeRefusal? _refused;

	public void Paused(string? recordingId, string reason)
	{
		lock (_sync)
		{
			if (_startedTicks != 0L)
			{
				_heldBy = reason;
				return;
			}
			_startedTicks = Math.Max(1L, Environment.TickCount64);
			_recordingId = (string.IsNullOrEmpty(recordingId) ? "-" : recordingId);
			_reason = reason;
			_heldBy = reason;
			_atStart = !_clipOpened;
			_refusals = 0L;
			_refused = null;
		}
	}

	public void Refused(ResumeRefusal refusal)
	{
		lock (_sync)
		{
			if (_startedTicks != 0L)
			{
				_refusals++;
				_refused = refusal;
			}
		}
	}

	public void Recording()
	{
		End(ParkEnd.Resumed, sessionEnded: false);
	}

	public void SessionEnded()
	{
		End(ParkEnd.SessionEnded, sessionEnded: true);
	}

	private void End(ParkEnd endedBy, bool sessionEnded)
	{
		TelemetryEvent telemetryEvent = null;
		lock (_sync)
		{
			if (_startedTicks != 0L)
			{
				long num = Math.Max(0L, Environment.TickCount64 - _startedTicks);
				if (num >= (long)ReportThreshold.TotalMilliseconds)
				{
					telemetryEvent = Ev.SessionParked(_recordingId, _reason, num, _atStart, endedBy, (_heldBy == _reason) ? null : _heldBy, (_refusals > 0) ? new long?(_refusals) : ((long?)null), _refused);
				}
				_startedTicks = 0L;
			}
			_clipOpened = !sessionEnded;
		}
		if (telemetryEvent != null)
		{
			Telemetry.Emit(telemetryEvent);
		}
	}
}
