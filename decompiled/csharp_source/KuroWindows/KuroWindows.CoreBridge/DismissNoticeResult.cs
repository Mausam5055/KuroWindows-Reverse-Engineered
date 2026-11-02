using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record DismissNoticeResult([property: JsonPropertyName("dismissed_notice_ids")] string[] DismissedNoticeIds);
