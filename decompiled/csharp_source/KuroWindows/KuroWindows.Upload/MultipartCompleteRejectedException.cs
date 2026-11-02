using System;

namespace KuroWindows.Upload;

internal sealed class MultipartCompleteRejectedException(string fileName, string code) : Exception("desktop-v2-upload-multipart-complete rejected " + fileName + ": " + code)
{
	public string FileName { get; } = fileName;

	public string Code { get; } = code;
}
