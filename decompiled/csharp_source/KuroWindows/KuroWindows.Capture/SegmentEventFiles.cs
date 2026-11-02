using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Capture;

internal sealed class SegmentEventFiles : IDisposable
{
	private static readonly byte[] Bom = new byte[3] { 239, 187, 191 };

	private static readonly byte[] Crlf = new byte[2] { 13, 10 };

	private readonly object _sync = new object();

	private readonly int[] _displayIndex;

	private readonly FileStream[] _streams;

	private readonly bool[] _touched;

	private readonly long[] _rows;

	private readonly long[] _lastSeq;

	private readonly long[] _bytes;

	private volatile bool _closed;

	private volatile bool _faulted;

	public int Count => _streams.Length;

	public bool Closed => _closed;

	public SegmentEventFiles(string sessionDir, IReadOnlyList<SegmentDisplayManifest> displays)
	{
		_displayIndex = new int[displays.Count];
		_streams = new FileStream[displays.Count];
		_touched = new bool[displays.Count];
		_rows = new long[displays.Count];
		_lastSeq = new long[displays.Count];
		_bytes = new long[displays.Count];
		try
		{
			for (int i = 0; i < displays.Count; i++)
			{
				_displayIndex[i] = displays[i].DisplayIndex;
				FileStream fileStream = new FileStream(Path.Combine(sessionDir, displays[i].EventsFile), FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
				_streams[i] = fileStream;
				fileStream.Write(Bom);
				_bytes[i] = Bom.Length;
				_touched[i] = true;
			}
		}
		catch
		{
			try
			{
				Close();
			}
			catch
			{
			}
			throw;
		}
	}

	public bool AppendAll(ReadOnlySpan<byte> line, long seq)
	{
		lock (_sync)
		{
			if (_closed || _faulted)
			{
				return false;
			}
			for (int i = 0; i < _streams.Length; i++)
			{
				AppendLocked(i, line, seq);
			}
			return true;
		}
	}

	public bool Append(int displayIndex, ReadOnlySpan<byte> line, long seq)
	{
		lock (_sync)
		{
			if (_closed || _faulted)
			{
				return false;
			}
			int num = SlotOf(displayIndex);
			if (num < 0)
			{
				return false;
			}
			AppendLocked(num, line, seq);
			return true;
		}
	}

	private void AppendLocked(int slot, ReadOnlySpan<byte> line, long seq)
	{
		try
		{
			FileStream obj = _streams[slot];
			obj.Write(line);
			obj.Write(Crlf);
		}
		catch
		{
			_faulted = true;
			throw;
		}
		_rows[slot]++;
		_lastSeq[slot] = seq;
		_bytes[slot] += line.Length + Crlf.Length;
		_touched[slot] = true;
	}

	public void Flush()
	{
		lock (_sync)
		{
			if (!_closed && !_faulted)
			{
				FlushLocked();
			}
		}
	}

	private void FlushLocked()
	{
		for (int i = 0; i < _streams.Length; i++)
		{
			if (_touched[i])
			{
				_touched[i] = false;
				try
				{
					_streams[i].Flush();
				}
				catch
				{
					_faulted = true;
					throw;
				}
			}
		}
	}

	public long BytesWritten(int displayIndex)
	{
		int num = SlotOf(displayIndex);
		if (num >= 0)
		{
			return Volatile.Read(in _bytes[num]);
		}
		return 0L;
	}

	public long Rows(int displayIndex)
	{
		int num = SlotOf(displayIndex);
		if (num >= 0)
		{
			return Volatile.Read(in _rows[num]);
		}
		return 0L;
	}

	public long LastSeq(int displayIndex)
	{
		int num = SlotOf(displayIndex);
		if (num >= 0)
		{
			return Volatile.Read(in _lastSeq[num]);
		}
		return 0L;
	}

	public void Close()
	{
		lock (_sync)
		{
			if (_closed)
			{
				return;
			}
			_closed = true;
			Exception ex = null;
			for (int i = 0; i < _streams.Length; i++)
			{
				FileStream fileStream = _streams[i];
				if (fileStream == null)
				{
					continue;
				}
				try
				{
					fileStream.Flush();
				}
				catch (Exception ex2)
				{
					if (ex == null)
					{
						ex = ex2;
					}
				}
				try
				{
					fileStream.Dispose();
				}
				catch (Exception ex3)
				{
					if (ex == null)
					{
						ex = ex3;
					}
				}
			}
			if (ex == null)
			{
				return;
			}
			_faulted = true;
			throw ex;
		}
	}

	public void Dispose()
	{
		Close();
	}

	private int SlotOf(int displayIndex)
	{
		for (int i = 0; i < _displayIndex.Length; i++)
		{
			if (_displayIndex[i] == displayIndex)
			{
				return i;
			}
		}
		return -1;
	}
}
