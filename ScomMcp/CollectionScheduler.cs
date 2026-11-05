namespace ScomMcp;

public sealed class CollectionScheduleSettings
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan AttemptDeadline { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxFailureBackoff { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxConsecutiveDeferredAttempts { get; set; } = 5;
    public void Validate()
    {
        if (Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromDays(1) ||
            AttemptDeadline < TimeSpan.FromSeconds(1) || AttemptDeadline > TimeSpan.FromHours(2) ||
            FailureBackoff < TimeSpan.FromSeconds(1) || MaxFailureBackoff < FailureBackoff ||
            MaxFailureBackoff > TimeSpan.FromDays(1) || MaxConsecutiveDeferredAttempts is < 1 or > 100)
            throw new InvalidOperationException("Collection schedule requires bounded intervals, deadlines and backoff.");
    }
}

public sealed class CollectionAttemptsExhaustedException : InvalidOperationException
{
    public CollectionAttemptsExhaustedException() :
        base("Collection reached its consecutive deferred-attempt limit.") { }
}

// The adapter must preserve/reconcile any ambiguous session before reporting
// deferred. Other errors stop the loop instead of starting a new source scan.
public enum CollectionAttemptOutcome { Published, Deferred }

public sealed class CollectionScheduler
{
    private readonly CollectionScheduleSettings settings;
    private readonly Func<CancellationToken, Task<CollectionAttemptOutcome>> attempt;
    private readonly ILogger<CollectionScheduler> logger;
    private int running;

    public CollectionScheduler(CollectionScheduleSettings settings,
        Func<CancellationToken, Task<CollectionAttemptOutcome>> attempt, ILogger<CollectionScheduler> logger)
    {
        settings.Validate();
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(logger);
        this.settings = new()
        {
            Interval = settings.Interval, AttemptDeadline = settings.AttemptDeadline,
            FailureBackoff = settings.FailureBackoff, MaxFailureBackoff = settings.MaxFailureBackoff,
            MaxConsecutiveDeferredAttempts = settings.MaxConsecutiveDeferredAttempts
        };
        this.attempt = attempt;
        this.logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Collection scheduler is already running.");
        var backoff = settings.FailureBackoff;
        try
        {
            var consecutiveDeferredAttempts = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(settings.AttemptDeadline);
                CollectionAttemptOutcome outcome;
                try
                {
                    // Await even noncooperative adapters: abandoning the task
                    // would permit another source request while it still runs.
                    outcome = await attempt(deadline.Token);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested)
                        throw new TimeoutException("Collection attempt exceeded its deadline.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError("Collection loop stopped. Event={Event} ErrorType={ErrorType}",
                        "SCOM_COLLECTION_STOPPED", exception.GetType().Name);
                    throw;
                }
                TimeSpan delay;
                switch (outcome)
                {
                    case CollectionAttemptOutcome.Published:
                        logger.LogInformation("Collection published. Event={Event}", "SCOM_COLLECTION_PUBLISHED");
                        delay = settings.Interval;
                        backoff = settings.FailureBackoff;
                        consecutiveDeferredAttempts = 0;
                        break;
                    case CollectionAttemptOutcome.Deferred:
                        consecutiveDeferredAttempts++;
                        logger.LogWarning("Collection deferred. Event={Event} Attempt={Attempt} MaxAttempts={MaxAttempts} DelaySeconds={DelaySeconds}",
                            "SCOM_COLLECTION_DEFERRED", consecutiveDeferredAttempts,
                            settings.MaxConsecutiveDeferredAttempts, backoff.TotalSeconds);
                        if (consecutiveDeferredAttempts >= settings.MaxConsecutiveDeferredAttempts)
                        {
                            logger.LogError("Collection loop stopped. Event={Event} ErrorType={ErrorType}",
                                "SCOM_COLLECTION_STOPPED", nameof(CollectionAttemptsExhaustedException));
                            throw new CollectionAttemptsExhaustedException();
                        }
                        delay = backoff;
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, settings.MaxFailureBackoff.Ticks));
                        break;
                    default:
                        throw new InvalidOperationException("Collection adapter returned an invalid outcome.");
                }
                // Fixed delay after completion coalesces missed periods rather
                // than accumulating a queue of overdue scans.
                await Task.Delay(delay, cancellationToken);
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
