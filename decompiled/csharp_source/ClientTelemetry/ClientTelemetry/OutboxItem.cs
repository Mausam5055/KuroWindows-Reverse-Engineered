using System;

namespace ClientTelemetry;

internal sealed record OutboxItem(Guid BatchId, string Path, byte[] EventsJson, int EventCount, string? SealedBy);
