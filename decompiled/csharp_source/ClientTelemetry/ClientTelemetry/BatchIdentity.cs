using System;

namespace ClientTelemetry;

internal sealed record BatchIdentity(string Platform, string AppName, string AppVersion, string? OsVersion, string? OsBuild, string? Arch, Guid InstallId);
