namespace ScomMcp;

public sealed record PreparedCollection(string SessionId, ReadModelManifest Manifest,
    IAsyncEnumerable<IngestionBatch> Batches);

public interface IScomCollectionSource
{
    Task<string> ReserveSessionAsync(CancellationToken cancellationToken);
    Task<string?> ReplaceExpiredEmptyReservationAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
    // Return the same prepared identity/data after ambiguous publication until
    // the outcome has been reconciled. Do not advance a source watermark here.
    Task<PreparedCollection> PrepareAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class CollectionWorker(IScomCollectionSource source, IngestionPublisher publisher)
{
    private int running;

    public async Task<CollectionAttemptOutcome> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Collection worker is already running.");
        try
        {
            var session = await source.ReserveSessionAsync(cancellationToken);
            try { await publisher.BeginAsync(session, cancellationToken); }
            catch (IngestionPublicationException error) when (error.Code == "PUBLISHER_SESSION_EXPIRED_EMPTY_RESERVATION")
            {
                // Only an authenticated receiver's exact empty-expired rejection
                // plus a durable source with no prepared payload permits replacement.
                var replacement = await source.ReplaceExpiredEmptyReservationAsync(session, cancellationToken);
                if (replacement is null) throw;
                session = replacement;
                await publisher.BeginAsync(session, cancellationToken);
            }
            var prepared = await source.PrepareAsync(cancellationToken);
            if (prepared is null || prepared.SessionId != session)
                throw new InvalidOperationException("Source preparation differs from its reserved session.");
            var committed = await publisher.PublishAsync(prepared.SessionId, prepared.Manifest, prepared.Batches, cancellationToken);
            if (committed != prepared.SessionId)
                throw new InvalidOperationException("Published identity differs from prepared collection.");
            // A cancellation or checkpoint-write failure must stop the loop.
            // Source acknowledgement remains retryable for this same session.
            await source.AcknowledgeAsync(committed, cancellationToken);
            return CollectionAttemptOutcome.Published;
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
