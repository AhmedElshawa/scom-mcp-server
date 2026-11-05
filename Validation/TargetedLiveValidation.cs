using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using ScomMcp;

static class TargetedLiveValidation
{
    public static async Task Run()
    {
        var assertions = 0;
        const string publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string objectId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        var clock = new Clock { Now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero) };
        var model = new ReadModelSettings { Enabled = true, ManagementGroupId = objectId, ScopeKind = "managementGroup", PublisherObjectIds = [publisher] };
        var settings = new TargetedLiveSettings { Enabled = true, PublisherObjectId = publisher, ProviderPolicySha256 = new('a', 64) };
        var caller = new Caller("synthetic-live-caller", ImmutableHashSet.Create(Wire.ScopeId));
        TargetedLiveChecks Broker(TargetedLiveSettings? policy = null) =>
            new(policy ?? settings, model, clock, NullLogger<TargetedLiveChecks>.Instance);
        var broker = Broker();
        var claim = new LiveClaim(settings.ProviderPolicySha256, broker.Source, true);
        settings.Validate(model);
        Invalid(() => new TargetedLiveSettings { Enabled = true }.Validate(model), "enabled requires pins");
        Invalid(() => new TargetedLiveSettings { MaxAlerts = 101 }.Validate(model), "alert budget validated");
        Invalid(() => new LiveWorkerSettings { Enabled = true }.Validate(), "unverified worker cannot enable");
        await Fault(() => Broker(new()).RequestAsync(caller, objectId, default), "LIVE_NOT_ENABLED", "disabled explicit");
        await Fault(() => broker.RequestAsync(caller, objectId, default), "LIVE_PROVIDER_UNAVAILABLE", "no heartbeat unavailable");
        await Fault(() => broker.RequestAsync(caller, "not-a-guid", default), "INVALID_ARGUMENT", "canonical ID required");
        await Fault(() => broker.RequestAsync(caller with { Scopes = ImmutableHashSet<string>.Empty }, objectId, default),
            "FORBIDDEN", "caller scope gate precedes source admission");
        SyncFault(() => broker.Claim("wrong", claim), "LIVE_PUBLISHER_FORBIDDEN", "publisher isolation");
        SyncFault(() => broker.Claim(publisher, claim with { ContractVerified = false }), "LIVE_PROVIDER_UNVERIFIED", "provider acceptance gate");
        SyncFault(() => broker.Claim(publisher, claim with { Source = claim.Source with { GroupId = objectId } }),
            "LIVE_PROVIDER_UNVERIFIED", "source binding independent");
        Equal(null, broker.Claim(publisher, claim), "empty poll does not invent data");
        using var cancelled = new CancellationTokenSource();
        var abandoned = broker.RequestAsync(caller, objectId, cancelled.Token);
        var shared = broker.RequestAsync(caller, objectId, default);
        await Fault(() => broker.RequestAsync(caller, objectId, default), "LIVE_RATE_LIMITED", "coalesced waiters bounded");
        cancelled.Cancel();
        try { await abandoned; throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { assertions++; }
        var request = broker.Claim(publisher, claim)!;
        Equal(objectId, request.ObjectId, "one exact target");
        Equal(3, request.MaxSourceRequests, "source request budget transmitted");
        Equal(null, broker.Claim(publisher, claim), "one in-flight lease");
        var good = Observation(request);
        SyncFault(() => broker.Complete(publisher, good with { ScopeVerified = false }), "LIVE_RESULT_INVALID", "fresh scope mandatory");
        SyncFault(() => broker.Complete(publisher, good with { ScopeCheckedAtUtc = clock.Now.AddSeconds(-1) }), "LIVE_RESULT_INVALID", "retained scope insufficient");
        SyncFault(() => broker.Complete(publisher, good with { ObjectId = publisher }), "LIVE_RESULT_INVALID", "target binding");
        SyncFault(() => broker.Complete(publisher, good with { SourceRequests = 4 }), "LIVE_RESULT_INVALID", "source work cap");
        SyncFault(() => broker.Complete(publisher, good with { CheckedAtUtc = clock.Now.AddSeconds(1) }), "LIVE_RESULT_INVALID", "future result rejected");
        SyncFault(() => broker.Complete(publisher, good with { HealthObservedAtUtc = clock.Now.AddSeconds(-1) }), "LIVE_RESULT_INVALID", "old health not live");
        var alert = new LiveAlert(publisher, objectId, "Synthetic", "critical", 0, clock.Now);
        SyncFault(() => broker.Complete(publisher, good with { Alerts = Enumerable.Repeat(alert, 21).ToImmutableArray() }),
            "LIVE_RESULT_INVALID", "overflow rejects not truncates");
        SyncFault(() => broker.Complete(publisher, good with { Alerts = [alert with { ObjectId = publisher }] }),
            "LIVE_RESULT_INVALID", "foreign alert cannot leak");
        broker.Complete(publisher, good);
        Equal(true, (await shared).AlertsComplete, "complete empty only from verified source");
        broker.Complete(publisher, good);
        SyncFault(() => broker.Complete(publisher, good with { AlertsComplete = false }), "LIVE_REPLAY_CONFLICT", "conflicting replay refused");

        clock.Now = clock.Now.AddSeconds(2);
        var partialTask = broker.RequestAsync(caller, publisher, default);
        var partialRequest = broker.Claim(publisher, claim)!;
        broker.Complete(publisher, Observation(partialRequest) with { AlertsComplete = false });
        Equal(false, (await partialTask).AlertsComplete, "partial empty does not mean no alerts");
        clock.Now = clock.Now.AddSeconds(2);
        var scopedOut = broker.RequestAsync(caller, objectId, default);
        var scopedRequest = broker.Claim(publisher, claim)!;
        broker.Complete(publisher, Observation(scopedRequest) with { Outcome = "outOfScope", ScopeVerified = false,
            Health = null, HealthObservedAtUtc = null, AlertsComplete = false });
        Equal("outOfScope", (await scopedOut).Outcome, "removed source scope has no success-shaped fallback");
        await Fault(() => broker.RequestAsync(caller, objectId, default), "LIVE_RATE_LIMITED", "coalescing used one admission, caller rate bounded");

        var expiredBroker = Broker();
        expiredBroker.Claim(publisher, claim);
        var pending = expiredBroker.RequestAsync(caller, objectId, default);
        var leased = expiredBroker.Claim(publisher, claim)!;
        clock.Now = clock.Now.AddSeconds(16);
        Equal(null, expiredBroker.Claim(publisher, claim), "timeout never reassigns ambiguous executing lease");
        await Fault(async () => { await pending; }, "LIVE_TIMEOUT", "expired request explicit");
        expiredBroker.Complete(publisher, Observation(leased) with { Outcome = "unavailable", ScopeVerified = false,
            Health = null, HealthObservedAtUtc = null, AlertsComplete = false });
        var next = expiredBroker.RequestAsync(caller, publisher, default);
        Equal(publisher, expiredBroker.Claim(publisher, claim)!.ObjectId, "joined late acknowledgement permits next distinct request");
        clock.Now = clock.Now.AddSeconds(16);
        expiredBroker.Claim(publisher, claim);
        await Fault(async () => { await next; }, "LIVE_TIMEOUT", "second timeout bounded");

        var queueBroker = Broker(new() { Enabled = true, PublisherObjectId = publisher,
            ProviderPolicySha256 = new('a', 64), QueueCapacity = 1 });
        queueBroker.Claim(publisher, claim);
        using var queueCancellation = new CancellationTokenSource();
        var queued = queueBroker.RequestAsync(caller, objectId, queueCancellation.Token);
        await Fault(() => queueBroker.RequestAsync(caller with { Binding = "different" }, publisher, default),
            "LIVE_QUEUE_FULL", "bounded queue across callers");
        queueCancellation.Cancel();
        try { await queued; } catch (OperationCanceledException) { assertions++; }
        Equal(null, queueBroker.Claim(publisher, claim), "cancel before claim causes zero source work");
        var isolated = Broker();
        isolated.Claim(publisher, claim);
        var a = isolated.RequestAsync(caller, objectId, default);
        var b = isolated.RequestAsync(caller with { Binding = "other" }, objectId, default);
        var ar = isolated.Claim(publisher, claim)!;
        isolated.Complete(publisher, Observation(ar));
        await a;
        Equal(false, b.IsCompleted, "no cross-caller coalescing/result leakage");
        clock.Now = clock.Now.AddSeconds(2);
        var br = isolated.Claim(publisher, claim)!;
        Equal(false, ar.RequestId == br.RequestId, "independent caller request identity");
        isolated.Complete(publisher, Observation(br));
        await b;
        Console.WriteLine($"PASS: {assertions} targeted-live broker assertions; disabled/unavailable, fresh scope, cancellation, caps, replay, isolation and no ambiguous reassignment.");

        LiveCheckObservation Observation(LiveCheckRequest r) => new(r.RequestId, r.ObjectId, r.Source, "ok", true,
            clock.Now, clock.Now, "critical", clock.Now, true, [], 3);
        void Equal<T>(T expected, T actual, string label)
        {
            assertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Live validation: " + label);
        }
        async Task Fault(Func<Task> run, string code, string label)
        {
            try { await run(); } catch (QueryFault ex) when (ex.Code == code) { assertions++; return; }
            throw new InvalidOperationException("Expected live fault: " + label);
        }
        void SyncFault(Action run, string code, string label)
        {
            try { run(); } catch (QueryFault ex) when (ex.Code == code) { assertions++; return; }
            throw new InvalidOperationException("Expected live fault: " + label);
        }
        void Invalid(Action run, string label)
        {
            try { run(); } catch (InvalidOperationException) { assertions++; return; }
            throw new InvalidOperationException("Expected invalid live configuration: " + label);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
