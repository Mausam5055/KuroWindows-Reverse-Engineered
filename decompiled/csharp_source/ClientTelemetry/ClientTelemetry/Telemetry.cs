using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;

namespace ClientTelemetry;

public static class Telemetry
{
	private sealed record ScopeState(string? RecordingId, int? SegmentIndex);

	private sealed class ScopeRestore : IDisposable
	{
		private readonly ScopeState? _previous;

		public ScopeRestore(ScopeState? previous)
		{
			_previous = previous;
		}

		public void Dispose()
		{
			Scoped.Value = _previous;
		}
	}

	public const int ChannelCapacity = 2048;

	private static readonly object Sync = new object();

	private static readonly AsyncLocal<ScopeState?> Scoped = new AsyncLocal<ScopeState>();

	private static volatile ScopeState? Context;

	private static readonly Channel<TelemetryEvent> Intake = Channel.CreateBounded(new BoundedChannelOptions(2048)
	{
		FullMode = BoundedChannelFullMode.DropOldest,
		SingleReader = false,
		SingleWriter = false
	}, (TelemetryEvent _) =>
	{
		OnDropped();
	});

	private static Pipeline? _pipeline;

	private static Writer? _writer;

	private static TelemetryHost? _host;

	private static (TelemetryConfigWire? Wire, bool LoggingEnabled)? _pendingConfig;

	private static (string? User, string? Device, string? Org)? _pendingIdentity;

	private static long _droppedBeforeStart;

	private static volatile Gate _level = Gate.Required;

	private static volatile bool _enabled = true;

	public static Gate Level => _level;

	public static TelemetryConfig? CurrentConfig => _pipeline?.Config;

	public static bool Records(TelemetryEvent e)
	{
		if (_enabled)
		{
			return e.Gate <= _level;
		}
		return false;
	}

	public static void Start(TelemetryHost host)
	{
		lock (Sync)
		{
			if (_pipeline != null)
			{
				return;
			}
			try
			{
				_host = host;
				ITelemetryClock clock = host.Clock ?? new SystemClock();
				PersistentState state = PersistentState.Load(host.StateDirectory);
				Outbox outbox = new Outbox(Path.Combine(host.StateDirectory, "outbox"), host.LocalLog, 67108864L);
				ITransport transport = host.Transport ?? new HttpTransport(host.HttpHandler);
				(TelemetryConfigWire, bool)? pendingConfig = _pendingConfig;
				TelemetryConfig telemetryConfig;
				if (pendingConfig.HasValue)
				{
					(TelemetryConfigWire, bool) valueOrDefault = pendingConfig.GetValueOrDefault();
					telemetryConfig = TelemetryConfig.Resolve(valueOrDefault.Item1, valueOrDefault.Item2, host.IngestUrl);
				}
				else
				{
					telemetryConfig = TelemetryConfig.Defaults(host.IngestUrl);
				}
				TelemetryConfig telemetryConfig2 = telemetryConfig;
				Pipeline pipeline = new Pipeline(host, telemetryConfig2, clock, outbox, transport, state, Intake.Reader);
				(string, string, string)? pendingIdentity = _pendingIdentity;
				if (pendingIdentity.HasValue)
				{
					(string, string, string) valueOrDefault2 = pendingIdentity.GetValueOrDefault();
					pipeline.SetIdentity(valueOrDefault2.Item1, valueOrDefault2.Item2, valueOrDefault2.Item3);
				}
				for (long num = Interlocked.Exchange(ref _droppedBeforeStart, 0L); num > 0; num--)
				{
					pipeline.NoteChannelDrop();
				}
				_level = telemetryConfig2.Level;
				_enabled = telemetryConfig2.Enabled;
				_pipeline = pipeline;
				_writer = new Writer(Intake.Reader, pipeline, clock, host.LocalLog);
				_writer.Start();
			}
			catch (Exception ex)
			{
				host.LocalLog?.Invoke("telemetry start failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	public static void Emit(TelemetryEvent e)
	{
		if (!Records(e))
		{
			return;
		}
		ScopeState scopeState = Scoped.Value ?? Context;
		if ((object)scopeState != null)
		{
			TelemetryEvent telemetryEvent = e;
			if (telemetryEvent.RecordingId == null)
			{
				string text = (telemetryEvent.RecordingId = scopeState.RecordingId);
			}
			telemetryEvent = e;
			if (!telemetryEvent.SegmentIndex.HasValue)
			{
				int? num = (telemetryEvent.SegmentIndex = scopeState.SegmentIndex);
			}
		}
		Intake.Writer.TryWrite(e);
	}

	public static void Count(string counter, string? label, long by = 1L)
	{
		_pipeline?.Count(counter, label, by);
	}

	public static void Observe(string distribution, double value)
	{
		_pipeline?.Observe(distribution, value);
	}

	public static void Verbose(string area, string line, string? source = null)
	{
		if (_level >= Gate.Verbose)
		{
			Emit(Ev.DiagnosticsVerboseLine(area, line, source));
			Count("verbose_lines_sent", null, 1L);
		}
	}

	public static void SetContext(string? recordingId, int? segmentIndex)
	{
		Context = ((recordingId == null) ? null : new ScopeState(recordingId, segmentIndex));
	}

	public static IDisposable Scope(string? recordingId, int? segmentIndex)
	{
		ScopeState value = Scoped.Value;
		Scoped.Value = new ScopeState(recordingId ?? value?.RecordingId, segmentIndex ?? value?.SegmentIndex);
		return new ScopeRestore(value);
	}

	public static void SetIdentity(string? userId, string? deviceId, string? organizationId = null)
	{
		lock (Sync)
		{
			_pendingIdentity = (userId, deviceId, organizationId);
			_pipeline?.SetIdentity(userId, deviceId, organizationId);
		}
	}

	public static void ApplyConfig(TelemetryConfigWire? wire, bool loggingEnabled)
	{
		lock (Sync)
		{
			_pendingConfig = (wire, loggingEnabled);
			if (_pipeline != null && (object)_host != null)
			{
				TelemetryConfig telemetryConfig = TelemetryConfig.Resolve(wire, loggingEnabled, _host.IngestUrl);
				_pipeline.ApplyConfig(telemetryConfig);
				_level = telemetryConfig.Level;
				_enabled = telemetryConfig.Enabled;
			}
		}
	}

	public static void SetSendEnabled(bool enabled)
	{
		if (enabled)
		{
			_pipeline?.EnableSending();
		}
	}

	public static void FlushToDisk()
	{
		_pipeline?.FlushToDisk();
	}

	public static void Drain(TimeSpan budget)
	{
		_pipeline?.Drain(budget);
	}

	public static void Stop()
	{
		lock (Sync)
		{
			_writer?.Stop();
			_pipeline?.FlushToDisk();
			_writer = null;
			_pipeline = null;
		}
	}

	private static void OnDropped()
	{
		Pipeline pipeline = _pipeline;
		if (pipeline == null)
		{
			Interlocked.Increment(ref _droppedBeforeStart);
		}
		else
		{
			pipeline.NoteChannelDrop();
		}
	}
}
