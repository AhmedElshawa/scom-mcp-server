using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScomMcp;

static class ExpiredReservationChecks
{
    private const string Publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Group = "22222222-2222-4222-8222-222222222222";
    private const string Policy = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task Run(string root)
    {
        var assertions = 0;
        var clock = new Clock();
        var receiver = new ReadModelStore(new()
        {
            Enabled = true, DatabasePath = Path.Combine(root, "expired-reservation-receiver.db"),
            ManagementGroupId = Group, ScopeKind = "managementGroup", PublisherObjectIds = [Publisher],
            SessionMinutes = 1
        }, new() { SnapshotEnabled = false }, clock);
        var profile = new Profile(clock);
        var checkpointPath = Path.Combine(root, "expired-reservation-source.db");
        using var publisher = new IngestionPublisher(new()
        {
            Endpoint = new("https://synthetic.invalid"), MaxAttempts = 1
        }, _ => Task.FromResult("synthetic-token"), new Handler(receiver));
        string acknowledged;
        string expired;
        using (var checkpoint = new SourceCheckpointStore(checkpointPath, Policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
            await new CollectionWorker(source, publisher).RunOnceAsync(CancellationToken.None);
            acknowledged = receiver.Page(new("synthetic", ImmutableHashSet.Create(Wire.ScopeId)),
                "objects", null, 20, null).DatasetId;
            expired = await source.ReserveSessionAsync(CancellationToken.None);
            receiver.Begin(Publisher, expired);
        }
        clock.Now = clock.Now.AddMinutes(2);
        Check(profile.Calls == 1, "interrupted reservation has no source preparation");
        var oldHistory = CheckpointPayload(checkpointPath);
        profile.BeforeCollect = () =>
        {
            using var before = JsonDocument.Parse(oldHistory);
            using var after = JsonDocument.Parse(CheckpointPayload(checkpointPath));
            foreach (var field in new[] { "acknowledged", "priorAlerts", "priorMetrics", "priorScope" })
                Check(before.RootElement.GetProperty(field).GetRawText() ==
                    after.RootElement.GetProperty(field).GetRawText(),
                    "reservation replacement preserves durable acknowledgement and prior reconciliation history");
        };
        using (var checkpoint = new SourceCheckpointStore(checkpointPath, Policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
            Check(await source.ReserveSessionAsync(CancellationToken.None) == expired,
                "expired reservation is pinned across process restart");
            await new CollectionWorker(source, publisher).RunOnceAsync(CancellationToken.None);
            var current = receiver.Page(new("synthetic", ImmutableHashSet.Create(Wire.ScopeId)),
                "objects", null, 20, null).DatasetId;
            Check(current != expired && current != acknowledged && profile.Calls == 2,
                "receiver-proven empty expiry permits one fresh collection under a new identity");
            await source.AcknowledgeAsync(current, CancellationToken.None);
            Check(await source.ReplaceExpiredEmptyReservationAsync(current, CancellationToken.None) is null,
                "acknowledged identity cannot be replaced");
        }
        profile.BeforeCollect = null;
        try { receiver.Begin(Publisher, expired); throw new InvalidOperationException("Retired identity was reusable."); }
        catch (QueryFault fault) when (fault.Message == "SESSION_ALREADY_CONSUMED") { assertions++; }

        var ambiguousPath = Path.Combine(root, "committed-unacknowledged-source.db");
        string ambiguous;
        using (var checkpoint = new SourceCheckpointStore(ambiguousPath, Policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
            ambiguous = await source.ReserveSessionAsync(CancellationToken.None);
            await publisher.BeginAsync(ambiguous, CancellationToken.None);
            var prepared = await source.PrepareAsync(CancellationToken.None);
            await publisher.PublishAsync(ambiguous, prepared.Manifest, prepared.Batches);
        }
        var callsBeforeReplay = profile.Calls;
        clock.Now = clock.Now.AddMinutes(2);
        using (var checkpoint = new SourceCheckpointStore(ambiguousPath, Policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
            await new CollectionWorker(source, publisher).RunOnceAsync(CancellationToken.None);
            await source.AcknowledgeAsync(ambiguous, CancellationToken.None);
            Check(profile.Calls == callsBeforeReplay &&
                receiver.Page(new("synthetic", ImmutableHashSet.Create(Wire.ScopeId)),
                    "objects", null, 20, null).DatasetId == ambiguous,
                "restart reconciles committed-but-unacknowledged payload with the same identity and no source call");
        }

        foreach (var preparedLocally in new[] { true, false })
        {
            var path = Path.Combine(root, "expired-refusal-" + preparedLocally + ".db");
            string session;
            using (var checkpoint = new SourceCheckpointStore(path, Policy))
            {
                var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
                session = await source.ReserveSessionAsync(CancellationToken.None);
                receiver.Begin(Publisher, session);
                if (preparedLocally)
                    await source.PrepareAsync(CancellationToken.None);
                else
                    receiver.Append(Publisher, session, 0,
                        Encoding.UTF8.GetBytes(Wire.Encode(new IngestionBatch([], [], [], []))));
            }
            clock.Now = clock.Now.AddMinutes(2);
            var calls = profile.Calls;
            var payload = CheckpointPayload(path);
            using (var checkpoint = new SourceCheckpointStore(path, Policy))
            {
                var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
                try
                {
                    await new CollectionWorker(source, publisher).RunOnceAsync(CancellationToken.None);
                    throw new InvalidOperationException("Ambiguous/prepared reservation was rotated.");
                }
                catch (IngestionPublicationException error) when (error.Code == (preparedLocally
                    ? "PUBLISHER_SESSION_EXPIRED_EMPTY_RESERVATION" : "PUBLISHER_SESSION_REPLAY_OR_EXPIRED"))
                { assertions++; }
                Check(await source.ReserveSessionAsync(CancellationToken.None) == session && profile.Calls == calls,
                    "refusal retains exact identity without any new source call");
            }
            Check(payload.SequenceEqual(CheckpointPayload(path)), "refusal does not change checkpoint payload");
        }

        var committed = Guid.NewGuid().ToString("D");
        receiver.Begin(Publisher, committed);
        var manifest = new ReadModelManifest(Group, "managementGroup", null, true, [],
            new(clock.Now, true, null), new(clock.Now, true, null), new(clock.Now, true, null), clock.Now);
        receiver.Commit(Publisher, committed, Encoding.UTF8.GetBytes(Wire.Encode(manifest)));
        clock.Now = clock.Now.AddMinutes(2);
        Check(receiver.Begin(Publisher, committed) == committed,
            "committed session replay remains committed rather than being classified empty-expired");
        var foreign = Guid.NewGuid().ToString("D");
        receiver.Begin(Publisher, foreign);
        clock.Now = clock.Now.AddMinutes(2);
        try
        {
            receiver.Begin("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", foreign);
            throw new InvalidOperationException("Foreign publisher authorized reservation replacement.");
        }
        catch (QueryFault fault) when (fault.Message != "SESSION_EXPIRED_EMPTY_RESERVATION") { assertions++; }
        var repeatedRejection = new RejectedBeginHandler();
        using (var boundedPublisher = new IngestionPublisher(new()
        {
            Endpoint = new("https://synthetic.invalid"), MaxAttempts = 1
        }, _ => Task.FromResult("synthetic-token"), repeatedRejection))
        using (var checkpoint = new SourceCheckpointStore(Path.Combine(root, "bounded-reservation.db"), Policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, checkpoint);
            var calls = profile.Calls;
            try
            {
                await new CollectionWorker(source, boundedPublisher).RunOnceAsync(CancellationToken.None);
                throw new InvalidOperationException("Repeated conflict was swallowed.");
            }
            catch (IngestionPublicationException error) when (error.Code == "PUBLISHER_SESSION_EXPIRED_EMPTY_RESERVATION")
            { assertions++; }
            Check(repeatedRejection.Requests == 2 && profile.Calls == calls,
                "one recovery per attempt cannot become an unbounded begin/replacement loop");
        }
        Console.WriteLine($"PASS: {assertions} expired reservation recovery assertions; restart, empty expiry, ledger preservation and ambiguous/prepared refusal.");

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
    }

    private static byte[] CheckpointPayload(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM source_checkpoint WHERE id=1";
        return (byte[])command.ExecuteScalar()!;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Profile(Clock clock) : IScomNormalizationProfile
    {
        public int Calls;
        public Action? BeforeCollect;
        public Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken token)
        {
            BeforeCollect?.Invoke();
            Calls++;
            return Task.FromResult(new NormalizedSourceScan(Group, "managementGroup", null, true, [], [], [],
                new(clock.Now, true, null), [], new(clock.Now, true, null), [], clock.Now));
        }
    }

    private sealed class Handler(ReadModelStore store) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var parts = request.RequestUri!.AbsolutePath.Split('/');
            var session = parts[3];
            object value;
            try
            {
                if (parts.Length == 4) value = new { sessionId = store.Begin(Publisher, session) };
                else if (parts[4] == "batches")
                {
                    store.Append(Publisher, session, int.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture),
                        await request.Content!.ReadAsByteArrayAsync(token));
                    value = new { accepted = true };
                }
                else value = new { datasetId = store.Commit(Publisher, session,
                    await request.Content!.ReadAsByteArrayAsync(token)) };
            }
            catch (QueryFault fault)
            {
                return new(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(Wire.Encode(new
                    {
                        error = fault.Code, reason = fault.Message
                    }), Encoding.UTF8, "application/json")
                };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Wire.Encode(value), Encoding.UTF8, "application/json") };
        }

    }

    private sealed class RejectedBeginHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(Wire.Encode(new
                {
                    error = "INGESTION_REJECTED", reason = "SESSION_EXPIRED_EMPTY_RESERVATION"
                }), Encoding.UTF8, "application/json")
            });
        }
    }
}
