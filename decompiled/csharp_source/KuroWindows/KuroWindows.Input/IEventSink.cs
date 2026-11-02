namespace KuroWindows.Input;

public interface IEventSink
{
	void Enqueue(in EventRow row);

	void Log(object payload, long timestampMs);
}
