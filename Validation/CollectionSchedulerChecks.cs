using Microsoft.Extensions.Logging;
using ScomMcp;

static class CollectionSchedulerChecks
{
    public static async Task Run()
    {
        var assertions = 0;
        var settings = new CollectionScheduleSettings
        {
            Interval = TimeSpan.FromSeconds(1), AttemptDeadline = TimeSpan.FromSeconds(1),
            FailureBackoff = TimeSpan.FromSeconds(1), MaxFailureBackoff = TimeSpan.FromSeconds(2)
        };
        var logger = new CapturedLogger();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var starts = new List<DateTimeOffset>();
        var active = 0;
        var maximum = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new CollectionScheduler(settings, async token =>
        {
            starts.Add(DateTimeOffset.UtcNow);
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            entered.TrySetResult();
            try { await Task.Delay(30, token); }
            finally { Interlocked.Decrement(ref active); }
            if (starts.Count == 4) stop.Cancel();
            return starts.Count <= 2 ? CollectionAttemptOutcome.Deferred : CollectionAttemptOutcome.Published;
        }, logger);
        var running = scheduler.RunAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await scheduler.RunAsync(stop.Token); throw new Exception("Concurrent loop accepted."); }
        catch (InvalidOperationException) { assertions++; }
        try { await running; throw new Exception("Expected stopped loop."); }
        catch (OperationCanceledException) { assertions++; }
        Check(starts.Count == 4 && maximum == 1, "one attempt at a time", ref assertions);
        Check(starts[1] - starts[0] >= TimeSpan.FromSeconds(1) &&
            starts[2] - starts[1] >= TimeSpan.FromSeconds(2) &&
            starts[3] - starts[2] >= TimeSpan.FromSeconds(1),
            "deferred backoff doubles and successful publication returns to normal cadence", ref assertions);
        Check(logger.Messages.Count(message => message.Contains("SCOM_COLLECTION_DEFERRED", StringComparison.Ordinal)) == 2,
            "deferred outcomes logged explicitly", ref assertions);
        var calls = 0;
        var failing = new CollectionScheduler(settings, _ =>
        {
            calls++;
            throw new IngestionPublicationException("SYNTHETIC_AMBIGUOUS_COMMIT");
        }, logger);
        try { await failing.RunAsync(CancellationToken.None); throw new Exception("Ambiguous failure swallowed."); }
        catch (IngestionPublicationException) { assertions++; }
        Check(calls == 1, "ambiguous publication stops without a new dataset/source scan", ref assertions);
        Check(!logger.Messages.Any(message => message.Contains("SYNTHETIC_AMBIGUOUS_COMMIT", StringComparison.Ordinal)),
            "diagnostics omit adapter exception payload", ref assertions);
        var finished = false;
        var noncooperative = new CollectionScheduler(settings, async _ =>
        {
            await Task.Delay(1200);
            finished = true;
            return CollectionAttemptOutcome.Published;
        }, logger);
        try { await noncooperative.RunAsync(CancellationToken.None); throw new Exception("Deadline not enforced."); }
        catch (TimeoutException) { assertions++; }
        Check(finished, "deadline waits for outstanding source work rather than abandoning it", ref assertions);
        Console.WriteLine($"PASS: {assertions} collection schedule assertions; serialization, cadence/backoff, deadlines and ambiguous-failure stop.");
    }

    private static void Check(bool condition, string message, ref int count)
    {
        if (!condition) throw new InvalidOperationException(message);
        count++;
    }
    private sealed class CapturedLogger : ILogger<CollectionScheduler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
