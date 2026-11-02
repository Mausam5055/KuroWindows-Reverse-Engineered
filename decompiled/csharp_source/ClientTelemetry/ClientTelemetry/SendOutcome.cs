using System;

namespace ClientTelemetry;

public readonly record struct SendOutcome(SendStatus Status, TimeSpan? RetryAfter = null, int HttpStatus = 0);
