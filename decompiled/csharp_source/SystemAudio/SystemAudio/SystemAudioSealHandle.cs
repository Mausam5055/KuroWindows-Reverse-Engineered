using System;
using System.Threading;

namespace SystemAudio;

public sealed class SystemAudioSealHandle
{
	private readonly ManualResetEventSlim _done = new ManualResetEventSlim(initialState: false);

	private readonly Func<SystemAudioFacts?>? _timedOut;

	private volatile SystemAudioFacts? _facts;

	private volatile string? _filePath;

	private volatile bool _timedOutRead;

	private int _completed;

	private int _outcomeRecorded;

	public static SystemAudioSealHandle None { get; } = CreateCompleted();

	public bool IsCompleted => _done.IsSet;

	public SystemAudioFacts? Facts => _facts;

	public string? FilePath
	{
		get
		{
			if (!_timedOutRead)
			{
				return _filePath;
			}
			return null;
		}
	}

	internal SystemAudioSealHandle(Func<SystemAudioFacts?>? timedOut)
	{
		_timedOut = timedOut;
	}

	public bool Wait(TimeSpan timeout)
	{
		return _done.Wait(timeout);
	}

	public SystemAudioFacts? TimedOut()
	{
		SystemAudioFacts systemAudioFacts;
		try
		{
			systemAudioFacts = _timedOut?.Invoke();
		}
		catch
		{
			return null;
		}
		if ((object)systemAudioFacts != null)
		{
			_timedOutRead = true;
			RecordOutcome(succeeded: false, systemAudioFacts.Reason);
		}
		return systemAudioFacts;
	}

	internal void RecordOutcome(bool succeeded, string? reason)
	{
		if (Interlocked.Exchange(ref _outcomeRecorded, 1) == 0)
		{
			SystemAudioPolicy.RecordSegmentOutcome(succeeded, reason);
		}
	}

	internal void Complete(SystemAudioFacts? facts, string? filePath)
	{
		if (Interlocked.Exchange(ref _completed, 1) == 0)
		{
			_facts = facts;
			_filePath = filePath;
			_done.Set();
		}
	}

	private static SystemAudioSealHandle CreateCompleted()
	{
		SystemAudioSealHandle systemAudioSealHandle = new SystemAudioSealHandle(null);
		systemAudioSealHandle.Complete(null, null);
		return systemAudioSealHandle;
	}
}
