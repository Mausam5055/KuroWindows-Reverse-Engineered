using System;
using System.IO;
using System.Threading;
using ClientTelemetry;

namespace KuroWindows.Upload;

internal sealed class UploadOpportunityClock
{
	internal const string FileName = ".upload-opportunity-seconds.txt";

	internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1.0);

	private readonly string _path;

	private readonly Func<bool> _usable;

	private readonly TimeProvider _time;

	private readonly object _gate = new object();

	private TimeSpan _elapsed;

	private long _lastTick;

	private long? _pausedSince;

	private ITimer? _timer;

	public long Seconds
	{
		get
		{
			lock (_gate)
			{
				return (long)_elapsed.TotalSeconds;
			}
		}
	}

	public UploadOpportunityClock(string directory, Func<bool> usable, TimeProvider? time = null)
	{
		_path = Path.Combine(directory, ".upload-opportunity-seconds.txt");
		_usable = usable;
		_time = time ?? TimeProvider.System;
	}

	public void Start()
	{
		lock (_gate)
		{
			if (_timer == null)
			{
				TimeSpan timeSpan = Load();
				if (timeSpan > _elapsed)
				{
					_elapsed = timeSpan;
				}
				_lastTick = _time.GetTimestamp();
				_timer = _time.CreateTimer((object? _) =>
				{
					Tick();
				}, null, TickInterval, TickInterval);
			}
		}
	}

	public void Stop()
	{
		ITimer timer;
		lock (_gate)
		{
			timer = _timer;
			_timer = null;
		}
		timer?.Dispose();
	}

	internal void Tick()
	{
		bool flag;
		try
		{
			flag = _usable();
		}
		catch
		{
			flag = false;
		}
		lock (_gate)
		{
			long timestamp = _time.GetTimestamp();
			TimeSpan elapsedTime = _time.GetElapsedTime(_lastTick, timestamp);
			_lastTick = timestamp;
			long? pausedSince;
			if (!flag)
			{
				pausedSince = _pausedSince;
				if (!pausedSince.HasValue)
				{
					_pausedSince = timestamp;
					Count.UploadPauseOpened(1L);
					KuroLog.LocalOnly("upload", "upload opportunity paused (signed out, network down or the platform not answering)", "Tick", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadOpportunityClock.cs", 96);
				}
				return;
			}
			pausedSince = _pausedSince;
			if (pausedSince.HasValue)
			{
				long valueOrDefault = pausedSince.GetValueOrDefault();
				TimeSpan elapsedTime2 = _time.GetElapsedTime(valueOrDefault, timestamp);
				_pausedSince = null;
				Observe.UploadPauseS(elapsedTime2.TotalSeconds);
				KuroLog.LocalOnly("upload", $"upload opportunity resumed after {elapsedTime2.TotalSeconds:F0}s", "Tick", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadOpportunityClock.cs", 106);
			}
			_elapsed += ((elapsedTime < TickInterval) ? elapsedTime : TickInterval);
			Persist();
		}
	}

	private TimeSpan Load()
	{
		try
		{
			long result;
			return (File.Exists(_path) && long.TryParse(File.ReadAllText(_path).Trim(), out result) && result > 0) ? TimeSpan.FromSeconds(result) : TimeSpan.Zero;
		}
		catch
		{
			return TimeSpan.Zero;
		}
	}

	private void Persist()
	{
		try
		{
			string text = $"{_path}.{Environment.ProcessId}.tmp";
			File.WriteAllText(text, ((long)_elapsed.TotalSeconds).ToString());
			File.Move(text, _path, overwrite: true);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("upload", "upload opportunity clock persist failed: " + ex.Message, "Persist", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadOpportunityClock.cs", 139);
		}
	}
}
