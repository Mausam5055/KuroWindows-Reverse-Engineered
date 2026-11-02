using System;
using System.Collections.Generic;

namespace ClientTelemetry;

internal sealed record BatchHeader(Guid BatchId, DateTimeOffset SentAt, string? UserId, string? DeviceId, string? OrganizationId, IReadOnlyDictionary<string, long> Health);
