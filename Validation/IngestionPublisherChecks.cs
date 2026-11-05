using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using ScomMcp;

static class IngestionPublisherChecks
{
    private const string Publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Group = "22222222-2222-4222-8222-222222222222";
    private const string ObjectId = "00000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly ReadModelManifest Manifest = new(Group, "managementGroup", null, true, [ObjectId],
        new(Now, true, null), new(Now, true, null), new(Now, true, null), Now);
    private static readonly IngestionBatch Batch = new(
        [new(ObjectId, "Synthetic.Entity", [], "synthetic", "unknown", Now, ImmutableDictionary<string, string?>.Empty)],
        [], [], []);
    private static int assertions;

    public static async Task Run(string root)
    {
        var store = new ReadModelStore(new()
        {
            Enabled = true, DatabasePath = Path.Combine(root, "publisher.db"),
            ManagementGroupId = Group, ScopeKind = "managementGroup", PublisherObjectIds = [Publisher]
        }, new() { SnapshotEnabled = false }, new Clock());
        var tokens = 0;
        var handler = new StoreHandler(store);
        using (var publisher = new IngestionPublisher(Settings(), _ =>
        {
            tokens++;
            return Task.FromResult("synthetic-token");
        }, handler))
        {
            var session = Guid.NewGuid().ToString("D");
            Check(await publisher.PublishAsync(session, Manifest, Batches(Batch)) == session, "publisher commits retained dataset");
            var page = store.Page(new("synthetic", ImmutableHashSet.Create(Wire.ScopeId)), "objects", null, 20, null);
            Check(page.TotalCount == 1 && page.DatasetId == session, "published objects query committed store");
            Check(handler.BatchAttempts == 2 && handler.CommitAttempts == 2 && tokens == 5,
                "lost accepted batch and commit responses retry with fresh token");
            Check(handler.Routes.All(p => !p.EndsWith("/fail", StringComparison.Ordinal)),
                "ambiguous commit never marks dataset failed");
            Check(handler.BatchBytes.Count == 2 && handler.BatchBytes[0].SequenceEqual(handler.BatchBytes[1]),
                "retried batch preserves exact bytes");
        }
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Conflict,
            HttpStatusCode.Redirect })
        {
            var reject = new ReplyHandler(_ => new HttpResponseMessage(code));
            using var publisher = new IngestionPublisher(Settings(), _ => Task.FromResult("synthetic-token"), reject);
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(Batch)),
                "PUBLISHER_HTTP_" + (int)code, "permanent status is not retried");
            Check(reject.Attempts == 1, "no retry or redirect for permanent response");
        }
        var unavailable = new ReplyHandler(_ => new(HttpStatusCode.ServiceUnavailable));
        var oneAttempt = Settings();
        oneAttempt.MaxAttempts = 1;
        using (var publisher = new IngestionPublisher(oneAttempt, _ => Task.FromResult("synthetic-token"), unavailable))
        {
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(Batch)),
                "PUBLISHER_RETRY_EXHAUSTED", "retry attempts are bounded");
            Check(unavailable.Attempts == 1, "max attempts exact boundary");
        }
        var invalid = new ReplyHandler(_ => Json(new { sessionId = "wrong" }));
        using (var publisher = new IngestionPublisher(Settings(), _ => Task.FromResult("synthetic-token"), invalid))
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(Batch)),
                "PUBLISHER_RESPONSE_INVALID", "success-shaped wrong identity denied");
        var oversized = new ReplyHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) });
        using (var publisher = new IngestionPublisher(Settings(), _ => Task.FromResult("synthetic-token"), oversized))
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(Batch)),
                "PUBLISHER_RESPONSE_INVALID", "response bytes bounded");
        var requests = new ReplyHandler(request => Json(new { sessionId = request.RequestUri!.Segments.Last() }));
        var small = Settings();
        small.MaxBatchBytes = 1024;
        using (var publisher = new IngestionPublisher(small, _ => Task.FromResult("synthetic-token"), requests))
        {
            var tooBig = Batch with { Objects = [Batch.Objects[0] with { DisplayName = new string('x', 2000) }] };
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(tooBig)),
                "PUBLISHER_PAYLOAD_BUDGET", "oversized serialization stops before batch request");
            Check(requests.Attempts == 1, "oversized batch never transmitted");
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"),
                Manifest with { MembershipVerified = false }, Batches(Batch)),
                "PUBLISHER_SCOPE_UNVERIFIED", "unverified membership never begins ingestion");
            Check(requests.Attempts == 1, "invalid scope sends no requests");
        }
        var countSettings = Settings();
        countSettings.MaxRecords = 1;
        var recordHandler = new ReplyHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("/batches/", StringComparison.Ordinal)
                ? Json(new { accepted = true }) : Json(new { sessionId = request.RequestUri!.Segments.Last() }));
        using (var publisher = new IngestionPublisher(countSettings, _ => Task.FromResult("synthetic-token"), recordHandler))
        {
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(Batch, Batch)),
                "PUBLISHER_RECORD_BUDGET", "aggregate record budget before extra upload");
            Check(recordHandler.Attempts == 2, "over-budget second batch and commit not sent");
        }
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var publisher = new IngestionPublisher(Settings(), _ => Task.FromResult("synthetic-token"),
            new ReplyHandler(request => Json(new { sessionId = request.RequestUri!.Segments.Last() }))))
        using (var cancellation = new CancellationTokenSource())
        {
            var running = publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Hold(held, cancellation.Token),
                cancellation.Token);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Fault(() => publisher.PublishAsync(Guid.NewGuid().ToString("D"), Manifest, Batches(Batch)),
                "PUBLISHER_ALREADY_RUNNING", "no overlapping publisher runs");
            cancellation.Cancel();
            try { await running; throw new InvalidOperationException("Expected publication cancellation."); }
            catch (OperationCanceledException) { assertions++; }
        }
        var http = Settings();
        http.Endpoint = new("http://synthetic.invalid");
        try { http.Validate(); throw new Exception("HTTP accepted."); }
        catch (InvalidOperationException) { assertions++; }
        foreach (var endpoint in new[] { "https://synthetic.invalid", "https://synthetic.invalid/",
            "https://synthetic.invalid/monitoring", "https://synthetic.invalid/monitoring/",
            "https://synthetic.invalid/platform/monitoring/" })
        {
            var pathSettings = Settings();
            pathSettings.Endpoint = new(endpoint);
            var prefix = pathSettings.Endpoint.AbsolutePath.TrimEnd('/');
            var routes = new List<string>();
            var batchAttempts = 0;
            var pathHandler = new ReplyHandler(request =>
            {
                Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "synthetic.invalid",
                    "application path preserves HTTPS authority");
                Check(request.Headers.Authorization?.Parameter == "synthetic-token",
                    "application path retains publisher authentication");
                routes.Add(request.RequestUri.AbsolutePath);
                if (request.RequestUri.AbsolutePath.Contains("/batches/", StringComparison.Ordinal))
                {
                    if (++batchAttempts == 1) return new(HttpStatusCode.ServiceUnavailable);
                    return Json(new { accepted = true });
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal))
                    return Json(new { datasetId = request.RequestUri.Segments[^2].TrimEnd('/') });
                return Json(new { sessionId = request.RequestUri.Segments.Last() });
            });
            using var pathPublisher = new IngestionPublisher(pathSettings, _ => Task.FromResult("synthetic-token"), pathHandler);
            var session = Guid.NewGuid().ToString("D");
            await pathPublisher.BeginAsync(session, CancellationToken.None);
            Check(await pathPublisher.PublishAsync(session, Manifest, Batches(Batch)) == session,
                "application base supports begin and publication");
            var route = prefix + "/ingestion/sessions/" + session;
            Check(routes.SequenceEqual(new[] { route, route, route + "/batches/0", route + "/batches/0", route + "/commit" }),
                "begin, batch, retry and commit preserve exact application prefix");
        }
        foreach (var endpoint in new[] { "https://user:password@synthetic.invalid/app/",
            "https://synthetic.invalid/app/?query=1", "https://synthetic.invalid/app/#fragment" })
        {
            var invalidEndpoint = Settings();
            invalidEndpoint.Endpoint = new(endpoint);
            try { invalidEndpoint.Validate(); throw new Exception("Unsafe endpoint accepted."); }
            catch (InvalidOperationException) { assertions++; }
        }
        Console.WriteLine($"PASS: {assertions} publisher assertions; offline publication, ambiguous responses, retry/auth/budgets and cancellation.");
    }

    private static IngestionPublisherSettings Settings() => new() { Endpoint = new("https://synthetic.invalid") };
    private static async IAsyncEnumerable<IngestionBatch> Batches(params IngestionBatch[] batches)
    {
        foreach (var batch in batches) yield return batch;
        await Task.CompletedTask;
    }
    private static async IAsyncEnumerable<IngestionBatch> Hold(TaskCompletionSource held,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        held.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        yield break;
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(Wire.Encode(value), Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
    private static async Task Fault(Func<Task> action, string code, string message)
    {
        try { await action(); }
        catch (IngestionPublicationException exception) when (exception.Code == code) { assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(reply(request));
        }
    }
    private sealed class StoreHandler(ReadModelStore store) : HttpMessageHandler
    {
        public int BatchAttempts;
        public int CommitAttempts;
        public List<byte[]> BatchBytes = [];
        public List<string> Routes = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Headers.Authorization?.Scheme == "Bearer" &&
                request.Headers.Authorization.Parameter == "synthetic-token", "publisher token applied per request");
            var path = request.RequestUri!.AbsolutePath;
            Routes.Add(path);
            var parts = path.Split('/');
            var session = parts[3];
            if (parts.Length == 4) return Json(new { sessionId = store.Begin(Publisher, session) });
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (parts[4] == "batches")
            {
                BatchBytes.Add(bytes);
                store.Append(Publisher, session, int.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture), bytes);
                if (++BatchAttempts == 1) throw new HttpRequestException("Synthetic lost response.");
                return Json(new { accepted = true });
            }
            var datasetId = store.Commit(Publisher, session, bytes);
            if (++CommitAttempts == 1) throw new HttpRequestException("Synthetic lost commit response.");
            return Json(new { datasetId });
        }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
