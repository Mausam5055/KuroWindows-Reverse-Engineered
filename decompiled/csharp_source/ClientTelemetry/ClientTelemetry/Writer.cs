using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;

namespace ClientTelemetry;

internal sealed class Writer
{
	private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(50.0);

	private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5.0);

	private readonly ChannelReader<TelemetryEvent> _intake;

	private readonly Pipeline _pipeline;

	private readonly ITelemetryClock _clock;

	private readonly Action<string>? _log;

	private readonly CancellationTokenSource _stop = new CancellationTokenSource();

	private Thread? _thread;

	public Writer(ChannelReader<TelemetryEvent> intake, Pipeline pipeline, ITelemetryClock clock, Action<string>? log)
	{
		_intake = intake;
		_pipeline = pipeline;
		_clock = clock;
		_log = log;
	}

	public void Start()
	{
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "telemetry",
			Priority = ThreadPriority.BelowNormal
		};
		_thread.Start();
	}

	public void Stop()
	{
		_stop.Cancel();
		_thread?.Join(TimeSpan.FromSeconds(2.0));
	}

	private void Run()
	{
		CancellationToken token = _stop.Token;
		while (!token.IsCancellationRequested)
		{
			try
			{
				_pipeline.Tick(token);
				TimeSpan timeout = TimeSpan.FromMilliseconds(Math.Clamp(_pipeline.NextDueMono() - _clock.MonoMs, (long)MinWait.TotalMilliseconds, (long)MaxWait.TotalMilliseconds));
				_intake.WaitToReadAsync(token).AsTask().Wait(timeout, token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				break;
			}
			catch (AggregateException ex2) when (ex2.InnerExceptions.All((Exception x) => x is OperationCanceledException))
			{
				break;
			}
			catch (Exception ex3)
			{
				_log?.Invoke("telemetry writer fault: " + ex3.GetType().Name + ": " + ex3.Message);
				Thread.Sleep(1000);
			}
		}
		_pipeline.FlushToDisk();
	}
}
