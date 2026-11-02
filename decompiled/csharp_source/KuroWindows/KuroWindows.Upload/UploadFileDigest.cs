using System.Collections.Generic;

namespace KuroWindows.Upload;

internal sealed record UploadFileDigest(string Sha256, IReadOnlyList<string>? PartSha256);
