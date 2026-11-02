using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace KuroWindows.Upload;

internal sealed class RangedProgressStreamContent : HttpContent
{
	private readonly Stream _source;

	private readonly long _length;

	private readonly int _bufferSize;

	private readonly Action<long, long>? _onProgress;

	public RangedProgressStreamContent(Stream source, long length, int bufferSize, Action<long, long>? onProgress)
	{
		_source = source;
		_length = length;
		_bufferSize = bufferSize;
		_onProgress = onProgress;
	}

	protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
	{
		byte[] buffer = new byte[_bufferSize];
		long sent = 0L;
		while (sent < _length)
		{
			int length = (int)Math.Min(buffer.Length, _length - sent);
			int read = await _source.ReadAsync(buffer.AsMemory(0, length)).ConfigureAwait(continueOnCapturedContext: false);
			if (read <= 0)
			{
				throw new EndOfStreamException($"Source ended after {sent} bytes while {_length} bytes were required.");
			}
			await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(continueOnCapturedContext: false);
			sent += read;
			_onProgress?.Invoke(sent, _length);
		}
	}

	protected override bool TryComputeLength(out long length)
	{
		length = _length;
		return true;
	}
}
