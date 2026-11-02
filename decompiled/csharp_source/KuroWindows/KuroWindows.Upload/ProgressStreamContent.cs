using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace KuroWindows.Upload;

internal sealed class ProgressStreamContent : HttpContent
{
	private readonly Stream _source;

	private readonly int _bufferSize;

	private readonly Action<long, long>? _onProgress;

	public ProgressStreamContent(Stream source, int bufferSize, Action<long, long>? onProgress)
	{
		_source = source;
		_bufferSize = bufferSize;
		_onProgress = onProgress;
	}

	protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
	{
		byte[] buffer = new byte[_bufferSize];
		long totalSent = 0L;
		long totalBytes = (_source.CanSeek ? _source.Length : (-1));
		while (true)
		{
			int num;
			int read = (num = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(continueOnCapturedContext: false));
			if (num <= 0)
			{
				break;
			}
			await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(continueOnCapturedContext: false);
			totalSent += read;
			_onProgress?.Invoke(totalSent, totalBytes);
		}
	}

	protected override bool TryComputeLength(out long length)
	{
		if (_source.CanSeek)
		{
			length = _source.Length;
			return true;
		}
		length = -1L;
		return false;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_source.Dispose();
		}
		base.Dispose(disposing);
	}
}
