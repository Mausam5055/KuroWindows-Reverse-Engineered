namespace ClientTelemetry;

public enum CrashKind
{
	UnhandledException,
	NativeSignal,
	WerDump,
	Metrickit,
	Hang
}
