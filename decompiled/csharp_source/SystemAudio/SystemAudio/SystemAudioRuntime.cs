using System;

namespace SystemAudio;

public static class SystemAudioRuntime
{
	private static volatile SystemAudioSettings? _settings;

	private static volatile SystemAudioLogSink? _capture;

	private static volatile SystemAudioLogSink? _local;

	private static volatile Action? _writerLeaked;

	private static volatile Action? _latched;

	private const string ConfigureFirst = "SystemAudioRuntime.Configure must run before the system audio module is used";

	internal static SystemAudioSettings Current => _settings ?? throw new InvalidOperationException("SystemAudioRuntime.Configure must run before the system audio module is used");

	internal static bool IsConfigured => (object)_settings != null;

	internal static SystemAudioLogSink? CaptureSink => _capture;

	internal static SystemAudioLogSink? LocalSink => _local;

	internal static Action? WriterLeaked => _writerLeaked;

	internal static Action? Latched => _latched;

	public static void Configure(SystemAudioSettings settings, SystemAudioLogSink capture, SystemAudioLogSink local, Action writerLeaked, Action latched)
	{
		ArgumentNullException.ThrowIfNull(settings, "settings");
		ArgumentNullException.ThrowIfNull(capture, "capture");
		ArgumentNullException.ThrowIfNull(local, "local");
		ArgumentNullException.ThrowIfNull(writerLeaked, "writerLeaked");
		ArgumentNullException.ThrowIfNull(latched, "latched");
		_settings = settings;
		_capture = capture;
		_local = local;
		_writerLeaked = writerLeaked;
		_latched = latched;
		SystemAudioPolicy.ApplyEnvironment(settings.EnvironmentOverrideRaw);
	}
}
