using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace ClientTelemetry;

internal sealed class Pipeline
{
	public const int MaxBatchEvents = 200;

	public const int MaxBatchBytes = 196608;

	public static readonly TimeSpan ErrorFastPath = TimeSpan.FromSeconds(60.0);

	public static readonly TimeSpan CountersInterval = TimeSpan.FromMinutes(15.0);

	private readonly object _sync = new object();

	private readonly TelemetryHost _host;

	private readonly ITelemetryClock _clock;

	private readonly Outbox _outbox;

	private readonly ITransport _transport;

	private readonly PersistentState _state;

	private readonly ChannelReader<TelemetryEvent> _intake;

	private readonly BatchIdentity _identity;

	private readonly CounterStore _counters = new CounterStore();

	private readonly ClockJumpDetector _jumps = new ClockJumpDetector();

	private readonly SignalDeduper _dedupe = new SignalDeduper();

	private readonly SendScheduler _scheduler;

	private readonly Random _random = new Random();

	private readonly Action<string>? _log;

	private readonly List<byte[]> _open = new List<byte[]>(200);

	private int _openBytes;

	private long _openedAtMono;

	private long _sealDeadlineMono;

	private long _errorDeadlineMono = long.MaxValue;

	private long _countersDueMono;

	private long? _budgetSignalWindowMono;

	private volatile TelemetryConfig _config;

	private bool _configLogged;

	private bool _sendEnabled;

	private string? _userId;

	private string? _deviceId;

	private string? _organizationId;

	private long _droppedChannel;

	private long _droppedQuota;

	private long _sendRejected;

	private long _retries;

	private long _clockJumps;

	private long _corruptDeleted;

	private bool CanSendNow
	{
		get
		{
			if (_sendEnabled && _config.Enabled)
			{
				return _userId != null;
			}
			return false;
		}
	}

	public TelemetryConfig Config => _config;

	public Pipeline(TelemetryHost host, TelemetryConfig config, ITelemetryClock clock, Outbox outbox, ITransport transport, PersistentState state, ChannelReader<TelemetryEvent> intake)
	{
		_host = host;
		_config = config;
		_clock = clock;
		_outbox = outbox;
		_transport = transport;
		_state = state;
		_deviceId = state.DeviceId;
		_intake = intake;
		_log = host.LocalLog;
		_identity = new BatchIdentity(host.Platform, host.AppName, host.AppVersion, host.OsVersion, host.OsBuild, host.Arch, state.InstallId);
		long monoMs = clock.MonoMs;
		_scheduler = new SendScheduler(monoMs, config.UploadInterval);
		_countersDueMono = monoMs + (long)CountersInterval.TotalMilliseconds;
		ResetOpen(monoMs);
		if (host.SendEnabled)
		{
			EnableSending();
		}
	}

	public void EnableSending()
	{
		lock (_sync)
		{
			if (!_sendEnabled)
			{
				_sendEnabled = true;
				_outbox.Recover();
			}
		}
	}

	public void ApplyConfig(TelemetryConfig config)
	{
		lock (_sync)
		{
			DrainIntakeLocked();
			TelemetryConfig config2 = _config;
			_config = config;
			if (config.UploadInterval != config2.UploadInterval)
			{
				_sealDeadlineMono = _openedAtMono + Jitter(config.UploadInterval);
				_scheduler.SetUploadInterval(config.UploadInterval);
			}
			if (!config.Enabled)
			{
				SealLocked(_clock.MonoMs);
			}
			if (!_configLogged || !(config == config2))
			{
				_configLogged = true;
				_log?.Invoke($"telemetry config enabled={config.Enabled} level={config.Level} interval_s={(int)config.UploadInterval.TotalSeconds} url={config.IngestUrl.Host}");
			}
		}
	}

	public void SetIdentity(string? userId, string? deviceId, string? organizationId)
	{
		lock (_sync)
		{
			if (userId != null && userId != _userId)
			{
				_scheduler.OnIdentity();
			}
			_userId = userId;
			_deviceId = deviceId ?? _state.DeviceId;
			_organizationId = organizationId;
		}
		if (deviceId != null)
		{
			_state.RememberDevice(deviceId);
		}
	}

	public void NoteChannelDrop()
	{
		Interlocked.Increment(ref _droppedChannel);
		_counters.Count("telemetry_dropped", "channel", 1L);
	}

	public void Count(string name, string? label, long by)
	{
		if (_config.Enabled)
		{
			_counters.Count(name, label, by);
		}
	}

	public void Observe(string name, double value)
	{
		if (_config.Enabled)
		{
			_counters.Observe(name, value);
		}
	}

	public void Accept(TelemetryEvent e)
	{
		lock (_sync)
		{
			AcceptLocked(e);
		}
	}

	public void Tick(CancellationToken ct = default(CancellationToken))
	{
		OutboxItem outboxItem = null;
		lock (_sync)
		{
			DrainIntakeLocked();
			long monoMs = _clock.MonoMs;
			foreach (TelemetryEvent item in _dedupe.Expire(monoMs))
			{
				Append(item, monoMs);
			}
			if (monoMs >= _countersDueMono)
			{
				FlushCounters(monoMs);
				_countersDueMono = monoMs + (long)CountersInterval.TotalMilliseconds;
			}
			if (monoMs >= _sealDeadlineMono || monoMs >= _errorDeadlineMono)
			{
				SealLocked(monoMs);
			}
			if (CanSendNow && _scheduler.CanSend(monoMs) && _outbox.HasPending)
			{
				outboxItem = TakeLocked();
			}
		}
		if ((object)outboxItem != null)
		{
			Send(outboxItem, ct);
		}
	}

	public long NextDueMono()
	{
		lock (_sync)
		{
			long monoMs = _clock.MonoMs;
			long val = _countersDueMono;
			if (_open.Count > 0)
			{
				val = Math.Min(val, Math.Min(_sealDeadlineMono, _errorDeadlineMono));
			}
			if (CanSendNow && _outbox.HasPending)
			{
				val = Math.Min(val, _scheduler.NextOpportunityMono(monoMs));
			}
			return Math.Max(val, monoMs);
		}
	}

	public void FlushToDisk()
	{
		lock (_sync)
		{
			DrainIntakeLocked();
			SealLocked(_clock.MonoMs);
		}
	}

	public void Drain(TimeSpan budget)
	{
		lock (_sync)
		{
			DrainIntakeLocked();
			long monoMs = _clock.MonoMs;
			foreach (TelemetryEvent item in _dedupe.Drain())
			{
				Append(item, monoMs);
			}
			FlushCounters(monoMs);
			SealLocked(monoMs);
		}
		if (budget <= TimeSpan.Zero)
		{
			return;
		}
		using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(budget);
		while (!cancellationTokenSource.IsCancellationRequested)
		{
			OutboxItem outboxItem;
			lock (_sync)
			{
				if (!CanSendNow || !_scheduler.CanDrain(_clock.MonoMs))
				{
					break;
				}
				outboxItem = TakeLocked();
			}
			if ((object)outboxItem == null || !Send(outboxItem, cancellationTokenSource.Token))
			{
				break;
			}
		}
	}

	private void DrainIntakeLocked()
	{
		TelemetryEvent item;
		while (_intake.TryRead(out item))
		{
			AcceptLocked(item);
		}
	}

	private void AcceptLocked(TelemetryEvent e)
	{
		if (!_config.Enabled || e.Gate > _config.Level)
		{
			return;
		}
		DateTimeOffset utcNow = _clock.UtcNow;
		long monoMs = _clock.MonoMs;
		Stamp(e, utcNow, monoMs);
		if (_jumps.Observe(utcNow.ToUnixTimeMilliseconds(), monoMs, out var wallDeltaMs, out var monoDeltaMs))
		{
			_clockJumps++;
			TelemetryEvent e2 = Ev.ClockJumped(wallDeltaMs, monoDeltaMs, ClockJumpSource.Detector);
			Stamp(e2, utcNow, monoMs);
			Append(e2, monoMs);
		}
		if (_dedupe.Admit(e, monoMs, out TelemetryEvent closed))
		{
			if (closed != null)
			{
				Append(closed, monoMs);
			}
			Append(e, monoMs);
		}
	}

	private void Stamp(TelemetryEvent e, DateTimeOffset now, long mono)
	{
		e.Id = UuidV7.New(now);
		e.Seq = _state.NextSeq();
		e.Time = now;
		e.MonoMs = mono;
	}

	private void Append(TelemetryEvent e, long mono)
	{
		byte[] array = Serializer.Event(e);
		if (_open.Count > 0 && _openBytes + array.Length + 1 > 196608)
		{
			SealLocked(mono);
		}
		_open.Add(array);
		_openBytes += array.Length + 1;
		if (e.Severity >= Severity.Error)
		{
			if (_errorDeadlineMono == long.MaxValue)
			{
				_errorDeadlineMono = mono + (long)ErrorFastPath.TotalMilliseconds;
			}
			_scheduler.Expedite(mono);
		}
		if (_open.Count >= 200)
		{
			SealLocked(mono);
		}
	}

	private void SealLocked(long mono)
	{
		if (_open.Count != 0)
		{
			byte[] eventsJson = Serializer.Events(_open);
			Guid batchId = UuidV7.New(_clock.UtcNow);
			int num = 0;
			try
			{
				num = _outbox.Seal(batchId, _clock.UtcNow, _state.NextSeq(), _identity.AppVersion, eventsJson);
				_state.Save();
			}
			catch (Exception ex)
			{
				_droppedQuota += _open.Count;
				_counters.Count("telemetry_dropped", "quota", _open.Count);
				_log?.Invoke("telemetry seal failed: " + ex.GetType().Name);
			}
			ResetOpen(mono);
			if (num > 0)
			{
				_droppedQuota += num;
				_counters.Count("telemetry_dropped", "quota", num);
				SignalBudgetExceeded(num, mono);
			}
		}
	}

	private void SignalBudgetExceeded(long dropped, long mono)
	{
		long? budgetSignalWindowMono = _budgetSignalWindowMono;
		if (budgetSignalWindowMono.HasValue)
		{
			long valueOrDefault = budgetSignalWindowMono.GetValueOrDefault();
			if ((double)(mono - valueOrDefault) < CountersInterval.TotalMilliseconds)
			{
				return;
			}
		}
		_budgetSignalWindowMono = mono;
		TelemetryEvent e = Ev.TelemetryBudgetExceeded(dropped, (long)CountersInterval.TotalSeconds);
		Stamp(e, _clock.UtcNow, mono);
		Append(e, mono);
	}

	private void FlushCounters(long mono)
	{
		if (!_counters.IsEmpty)
		{
			(IReadOnlyDictionary<string, object> Counters, IReadOnlyDictionary<string, object> Distributions) tuple = _counters.Snapshot();
			IReadOnlyDictionary<string, object> item = tuple.Counters;
			IReadOnlyDictionary<string, object> item2 = tuple.Distributions;
			Attributes attributes = new Attributes(3);
			attributes.Add("window_s", (long)CountersInterval.TotalSeconds);
			attributes.AddMap("counters", item);
			attributes.AddMap("distributions", item2);
			TelemetryEvent e = Catalog.CountersFlushed(attributes);
			Stamp(e, _clock.UtcNow, mono);
			Append(e, mono);
		}
	}

	private void ResetOpen(long mono)
	{
		_open.Clear();
		_openBytes = 1;
		_openedAtMono = mono;
		_sealDeadlineMono = mono + Jitter(_config.UploadInterval);
		_errorDeadlineMono = long.MaxValue;
	}

	private long Jitter(TimeSpan interval)
	{
		return (long)(interval.TotalMilliseconds * (0.8 + _random.NextDouble() * 0.4));
	}

	private OutboxItem? TakeLocked()
	{
		OutboxItem? result = _outbox.TakeOldest(out var corruptDeleted);
		if (corruptDeleted > 0)
		{
			_corruptDeleted += corruptDeleted;
			_counters.Count("telemetry_dropped", "rejected", corruptDeleted);
		}
		return result;
	}

	private bool Send(OutboxItem item, CancellationToken ct)
	{
		try
		{
			string text = null;
			try
			{
				text = _host.AccessToken(ct).AsTask().GetAwaiter()
					.GetResult();
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				_log?.Invoke("telemetry token unavailable: " + ex.GetType().Name);
			}
			if (string.IsNullOrEmpty(text))
			{
				lock (_sync)
				{
					_outbox.Release(item);
					_scheduler.OnNoToken(_clock.MonoMs);
				}
				return false;
			}
			Uri ingestUrl;
			byte[] array;
			lock (_sync)
			{
				ingestUrl = _config.IngestUrl;
				string sealedBy = item.SealedBy;
				array = Serializer.Body((sealedBy != null && sealedBy != _identity.AppVersion) ? _identity with
				{
					AppVersion = sealedBy
				} : _identity, new BatchHeader(item.BatchId, _clock.UtcNow, _userId, _deviceId, _organizationId, HealthSnapshot()), item.EventsJson);
			}
			SendOutcome sendOutcome = _transport.Send(ingestUrl, text, array, ct);
			lock (_sync)
			{
				long monoMs = _clock.MonoMs;
				switch (sendOutcome.Status)
				{
				case SendStatus.Accepted:
					_outbox.Complete(item);
					_scheduler.OnResponse(monoMs);
					ResetHealth();
					return true;
				case SendStatus.Rejected:
					_outbox.Complete(item);
					_sendRejected += item.EventCount;
					_counters.Count("telemetry_dropped", "rejected", item.EventCount);
					_scheduler.OnResponse(monoMs);
					_log?.Invoke($"telemetry batch rejected http={sendOutcome.HttpStatus} events={item.EventCount}");
					return true;
				case SendStatus.Throttled:
					_outbox.Release(item);
					_scheduler.OnThrottled(monoMs, sendOutcome.RetryAfter);
					return false;
				default:
					_outbox.Release(item);
					_retries++;
					_scheduler.OnFailure(monoMs, sendOutcome.RetryAfter);
					return false;
				}
			}
		}
		catch (Exception) when (ct.IsCancellationRequested)
		{
			lock (_sync)
			{
				_outbox.Release(item);
			}
			return false;
		}
	}

	private Dictionary<string, long> HealthSnapshot()
	{
		return new Dictionary<string, long>(StringComparer.Ordinal)
		{
			["dropped_channel"] = Interlocked.Read(in _droppedChannel),
			["dropped_quota"] = _droppedQuota,
			["send_rejected"] = _sendRejected,
			["retries"] = _retries,
			["clock_jumps"] = _clockJumps,
			["corrupt_deleted"] = _corruptDeleted,
			["pending_batches"] = _outbox.PendingCount
		};
	}

	private void ResetHealth()
	{
		Interlocked.Exchange(ref _droppedChannel, 0L);
		_droppedQuota = 0L;
		_sendRejected = 0L;
		_retries = 0L;
		_clockJumps = 0L;
		_corruptDeleted = 0L;
	}
}
