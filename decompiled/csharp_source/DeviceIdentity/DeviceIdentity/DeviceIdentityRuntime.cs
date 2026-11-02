using System;

namespace DeviceIdentity;

public static class DeviceIdentityRuntime
{
	private static volatile DeviceIdentitySettings? _settings;

	private static volatile DeviceIdentityLogSink? _auth;

	private const string ConfigureFirst = "DeviceIdentityRuntime.Configure must run before the device identity core is used";

	internal static DeviceIdentitySettings Current => _settings ?? throw new InvalidOperationException("DeviceIdentityRuntime.Configure must run before the device identity core is used");

	internal static DeviceIdentityLogSink? AuthSink => _auth;

	public static void Configure(DeviceIdentitySettings settings, DeviceIdentityLogSink auth)
	{
		ArgumentNullException.ThrowIfNull(settings, "settings");
		ArgumentNullException.ThrowIfNull(auth, "auth");
		_settings = settings;
		_auth = auth;
	}
}
