using System;

namespace ClientTelemetry;

internal sealed class SendScheduler
{
	public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60.0);

	public static readonly TimeSpan SendFloor = TimeSpan.FromSeconds(20.0);

	public const int BudgetCapacity = 8;

	public static readonly TimeSpan BudgetWindow = TimeSpan.FromMinutes(30.0);

	public static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(30.0);

	public static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(30.0);

	public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1.0);

	public static readonly TimeSpan DefaultThrottle = TimeSpan.FromSeconds(60.0);

	private readonly Random _random;

	private double _tokens = 8.0;

	private double _refillPerMs;

	private long _tokensRefilledAtMono;

	private long _startupUntilMono;

	private long _floorUntilMono;

	private long _retryAtMono;

	private long _throttledUntilMono;

	private int _failures;

	public SendScheduler(long nowMono, TimeSpan uploadInterval, Random? random = null)
	{
		_random = random ?? Random.Shared;
		_tokensRefilledAtMono = nowMono;
		_startupUntilMono = nowMono + (long)StartupDelay.TotalMilliseconds;
		SetUploadInterval(uploadInterval);
	}

	public void SetUploadInterval(TimeSpan uploadInterval)
	{
		_refillPerMs = Math.Max(8.0 / BudgetWindow.TotalMilliseconds, 4.0 / Math.Max(1.0, uploadInterval.TotalMilliseconds));
	}

	public bool CanSend(long nowMono)
	{
		Refill(nowMono);
		if (nowMono >= GateMono())
		{
			return _tokens >= 0.999999999;
		}
		return false;
	}

	public long NextOpportunityMono(long nowMono)
	{
		long val = GateMono();
		if (_tokens < 1.0)
		{
			val = Math.Max(val, nowMono + (long)((1.0 - _tokens) / _refillPerMs));
		}
		return Math.Max(val, nowMono);
	}

	public bool CanDrain(long nowMono)
	{
		return nowMono >= _throttledUntilMono;
	}

	public void Expedite(long nowMono)
	{
		_startupUntilMono = Math.Min(_startupUntilMono, nowMono);
	}

	public void OnResponse(long nowMono)
	{
		Charge(nowMono);
		_failures = 0;
	}

	public void OnFailure(long nowMono, TimeSpan? retryAfter)
	{
		Charge(nowMono);
		_failures++;
		TimeSpan timeSpan;
		if (retryAfter.HasValue)
		{
			TimeSpan valueOrDefault = retryAfter.GetValueOrDefault();
			timeSpan = Clamp(valueOrDefault, SendFloor, MaxRetryAfter);
		}
		else
		{
			timeSpan = FullJitter(_failures);
		}
		TimeSpan timeSpan2 = timeSpan;
		_retryAtMono = nowMono + (long)timeSpan2.TotalMilliseconds;
	}

	public void OnThrottled(long nowMono, TimeSpan? retryAfter)
	{
		Charge(nowMono);
		_throttledUntilMono = nowMono + (long)Clamp(retryAfter ?? DefaultThrottle, DefaultThrottle, MaxRetryAfter).TotalMilliseconds;
	}

	public void OnNoToken(long nowMono)
	{
		_failures++;
		_retryAtMono = nowMono + Math.Max((long)FullJitter(_failures).TotalMilliseconds, (long)SendFloor.TotalMilliseconds);
	}

	public void OnIdentity()
	{
		_failures = 0;
		_retryAtMono = 0L;
	}

	private long GateMono()
	{
		return Math.Max(Math.Max(_startupUntilMono, _floorUntilMono), Math.Max(_retryAtMono, _throttledUntilMono));
	}

	private void Charge(long nowMono)
	{
		_tokens--;
		_floorUntilMono = nowMono + (long)SendFloor.TotalMilliseconds;
	}

	private TimeSpan FullJitter(int failures)
	{
		double num = Math.Min(BackoffCap.TotalMilliseconds, BackoffBase.TotalMilliseconds * Math.Pow(2.0, failures));
		return TimeSpan.FromMilliseconds(_random.NextDouble() * num);
	}

	private void Refill(long nowMono)
	{
		long num = nowMono - _tokensRefilledAtMono;
		if (num > 0)
		{
			_tokens = Math.Min(8.0, _tokens + (double)num * _refillPerMs);
			_tokensRefilledAtMono = nowMono;
		}
	}

	private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max)
	{
		if (!(v < min))
		{
			if (!(v > max))
			{
				return v;
			}
			return max;
		}
		return min;
	}
}
