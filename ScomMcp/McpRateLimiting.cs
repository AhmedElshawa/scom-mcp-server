using System.Security.Claims;
using System.Threading.RateLimiting;

namespace ScomMcp;

// Defaults are the reviewed production limits. They are not bound to configuration;
// only the validation harness overrides them through the options pipeline.
public sealed class McpRateLimitOptions
{
    public int PermitLimit { get; set; } = 30;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    public int ConcurrencyLimit { get; set; } = 4;
}

internal static class McpRateLimiting
{
    public const string PolicyName = "ScomRead";

    public static string PartitionKey(ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value is { Length: > 0 } oid ? "oid:" + oid.ToLowerInvariant() : "unidentified";
}

// A permit is a request in the current window AND a free concurrency slot. Every attempt
// consumes a window permit first, so a caller cannot retry past the limit by being rejected
// on concurrency. There is no queue: a request that cannot start immediately is rejected.
internal sealed class WindowAndConcurrencyLimiter : RateLimiter
{
    private readonly FixedWindowRateLimiter window;
    private readonly ConcurrencyLimiter concurrency;

    public WindowAndConcurrencyLimiter(McpRateLimitOptions options)
    {
        window = new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit, Window = options.Window, QueueLimit = 0, AutoReplenishment = true
        });
        concurrency = new(new ConcurrencyLimiterOptions { PermitLimit = options.ConcurrencyLimit, QueueLimit = 0 });
    }

    public override RateLimiterStatistics? GetStatistics() => concurrency.GetStatistics();

    public override TimeSpan? IdleDuration
    {
        get
        {
            var a = window.IdleDuration;
            var b = concurrency.IdleDuration;
            return a is null || b is null ? null : a < b ? a : b;
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var windowLease = window.AttemptAcquire(permitCount);
        if (!windowLease.IsAcquired) return windowLease;
        var concurrencyLease = concurrency.AttemptAcquire(permitCount);
        if (!concurrencyLease.IsAcquired)
        {
            windowLease.Dispose();
            return concurrencyLease;
        }
        return new Lease(windowLease, concurrencyLease);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AttemptAcquireCore(permitCount));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            window.Dispose();
            concurrency.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class Lease(RateLimitLease windowLease, RateLimitLease concurrencyLease) : RateLimitLease
    {
        public override bool IsAcquired => true;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                concurrencyLease.Dispose();
                windowLease.Dispose();
            }
        }
    }
}
