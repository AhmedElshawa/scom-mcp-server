using System.Collections.Immutable;
using System.Net;
using System.Text;
using ScomMcp;

static class CollectionWorkerChecks
{
    public static async Task Run()
    {
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid().ToString("D");
        var manifest = new ReadModelManifest("22222222-2222-4222-8222-222222222222", "managementGroup", null, true, [],
            new(now, true, null), new(now, true, null), new(now, true, null), now);
        var source = new Source(new(id, manifest, Batches()));
        var handler = new Handler(source);
        source.BeforePrepare = () =>
        {
            if (!handler.Begun) throw new InvalidOperationException("Source collection ran before ingestion attempt began.");
        };
        using var publisher = new IngestionPublisher(new() { Endpoint = new("https://synthetic.invalid"), MaxAttempts = 1 },
            _ => Task.FromResult("synthetic-token"), handler);
        var worker = new CollectionWorker(source, publisher);
        var outcome = await worker.RunOnceAsync(CancellationToken.None);
        if (outcome != CollectionAttemptOutcome.Published || source.Acknowledgements != 1 ||
            source.Prepared != 1 || !handler.Committed)
            throw new InvalidOperationException("Source watermark must advance only after committed acknowledgement.");
        handler.RejectCommit = true;
        try { await worker.RunOnceAsync(CancellationToken.None); throw new Exception("Commit failure was swallowed."); }
        catch (IngestionPublicationException) { }
        if (source.Acknowledgements != 1)
            throw new InvalidOperationException("Failed publication advanced source watermark.");
        handler.RejectCommit = false;
        source.RejectAcknowledgement = true;
        try { await worker.RunOnceAsync(CancellationToken.None); throw new Exception("Checkpoint failure swallowed."); }
        catch (IOException) { }
        source.RejectAcknowledgement = false;
        await worker.RunOnceAsync(CancellationToken.None);
        if (source.Acknowledgements != 2 || source.Prepared != 4)
            throw new InvalidOperationException("Retry must reconcile same prepared session after checkpoint failure.");
        handler.Begun = false;
        source.RejectPreparation = true;
        try { await worker.RunOnceAsync(CancellationToken.None); throw new Exception("Preparation failure swallowed."); }
        catch (QueryFault) { }
        if (!handler.Begun || source.Acknowledgements != 2)
            throw new InvalidOperationException("Failed preparation must occur after begin without advancing checkpoint.");
        Console.WriteLine("PASS: 5 collection worker assertions; pre-collection begin, commit-gated checkpoint and failure recovery.");
    }
    private static async IAsyncEnumerable<IngestionBatch> Batches()
    {
        yield return new([], [], [], []);
        await Task.CompletedTask;
    }
    private sealed class Source(PreparedCollection collection) : IScomCollectionSource
    {
        public int Prepared;
        public int Acknowledgements;
        public bool RejectAcknowledgement;
        public bool RejectPreparation;
        public Action? BeforePrepare;
        public Task<string> ReserveSessionAsync(CancellationToken cancellationToken) => Task.FromResult(collection.SessionId);
        public Task<PreparedCollection> PrepareAsync(CancellationToken cancellationToken)
        {
            BeforePrepare?.Invoke();
            if (RejectPreparation) throw new QueryFault("SOURCE_UNAVAILABLE", "SYNTHETIC_SOURCE_FAILED");
            Prepared++;
            return Task.FromResult(collection);
        }
        public Task AcknowledgeAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (sessionId != collection.SessionId) throw new InvalidOperationException("Wrong checkpoint identity.");
            if (RejectAcknowledgement) throw new IOException("Synthetic checkpoint failure.");
            Acknowledgements++;
            return Task.CompletedTask;
        }
    }
    private sealed class Handler(Source source) : HttpMessageHandler
    {
        public bool Committed;
        public bool RejectCommit;
        public bool Begun;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (source.Acknowledgements > source.Prepared) throw new InvalidOperationException("Invalid checkpoint ordering.");
            object result;
            if (path.EndsWith("/commit", StringComparison.Ordinal))
            {
                if (RejectCommit) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
                Committed = true;
                result = new { datasetId = path.Split('/')[3] };
            }
            else if (path.Contains("/batches/", StringComparison.Ordinal)) result = new { accepted = true };
            else
            {
                Begun = true;
                result = new { sessionId = path.Split('/')[3] };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Wire.Encode(result), Encoding.UTF8, "application/json") });
        }
    }
}
