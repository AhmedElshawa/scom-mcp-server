using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ScomMcp;

public sealed class IngestionPublisherSettings
{
    public Uri Endpoint { get; set; } = null!;
    public int MaxBatchBytes { get; set; } = 1024 * 1024;
    public int MaxDatasetBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxRecords { get; set; } = 100000;
    public int MaxAttempts { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int PublicationTimeoutSeconds { get; set; } = 1200;
    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || Endpoint.Scheme != "https" ||
            Endpoint.AbsolutePath != "/" || Endpoint.UserInfo.Length != 0 ||
            Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0 ||
            MaxBatchBytes is < 1024 or > 1024 * 1024 ||
            MaxDatasetBytes < MaxBatchBytes || MaxDatasetBytes > 32 * 1024 * 1024 ||
            MaxRecords is < 1 or > 1000000 || MaxAttempts is < 1 or > 5 ||
            RequestTimeoutSeconds is < 1 or > 120 ||
            PublicationTimeoutSeconds < RequestTimeoutSeconds || PublicationTimeoutSeconds > 7200)
            throw new InvalidOperationException("Publisher settings require an HTTPS origin and bounded publication budgets.");
    }
}

public sealed class IngestionPublicationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// This transport is independent of the SCOM SDK. Tokens stay in memory and are
// acquired for each attempt; a lost commit response must never trigger /fail.
public sealed class IngestionPublisher : IDisposable
{
    private readonly IngestionPublisherSettings settings;
    private readonly Func<CancellationToken, Task<string>> acquireToken;
    private readonly HttpClient client;
    private int publishing;

    public IngestionPublisher(IngestionPublisherSettings settings, Func<CancellationToken, Task<string>> acquireToken)
        : this(settings, acquireToken, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    // An injected handler is for offline testing; it owns TLS/redirect behavior.
    public IngestionPublisher(IngestionPublisherSettings settings, Func<CancellationToken, Task<string>> acquireToken,
        HttpMessageHandler handler)
    {
        settings.Validate();
        ArgumentNullException.ThrowIfNull(acquireToken);
        ArgumentNullException.ThrowIfNull(handler);
        this.settings = new()
        {
            Endpoint = settings.Endpoint, MaxBatchBytes = settings.MaxBatchBytes,
            MaxDatasetBytes = settings.MaxDatasetBytes, MaxRecords = settings.MaxRecords,
            MaxAttempts = settings.MaxAttempts, RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            PublicationTimeoutSeconds = settings.PublicationTimeoutSeconds
        };
        this.acquireToken = acquireToken;
        client = new(handler) { BaseAddress = settings.Endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> PublishAsync(string sessionId, ReadModelManifest manifest,
        IAsyncEnumerable<IngestionBatch> batches, CancellationToken cancellationToken = default)
    {
        if (!ReadModelSettings.ValidId(sessionId) || Guid.Parse(sessionId).ToString("D") != sessionId)
            throw new IngestionPublicationException("PUBLISHER_SESSION_INVALID");
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(batches);
        if (!manifest.MembershipVerified)
            throw new IngestionPublicationException("PUBLISHER_SCOPE_UNVERIFIED");
        if (Interlocked.CompareExchange(ref publishing, 1, 0) != 0)
            throw new IngestionPublicationException("PUBLISHER_ALREADY_RUNNING");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.PublicationTimeoutSeconds));
        try
        {
            if (manifest.ExpectedObjectIds.IsDefault || manifest.ExpectedObjectIds.Length > settings.MaxRecords)
                throw new IngestionPublicationException("PUBLISHER_MEMBERSHIP_BUDGET");
            if (manifest.ExpectedObjectIds.Any(id => !ReadModelSettings.ValidId(id) ||
                Guid.Parse(id).ToString("D") != id) ||
                manifest.ExpectedObjectIds.Distinct(StringComparer.Ordinal).Count() != manifest.ExpectedObjectIds.Length)
                throw new IngestionPublicationException("PUBLISHER_MEMBERSHIP_INVALID");
            var manifestBytes = SerializeBounded(manifest);
            long totalBytes = manifestBytes.Length;
            long records = 0;
            var route = "ingestion/sessions/" + sessionId;
            await Send(route, null, "sessionId", sessionId, deadline.Token);
            var sequence = 0;
            await foreach (var batch in batches.WithCancellation(deadline.Token))
            {
                if (batch is null || batch.Objects.IsDefault || batch.Relationships.IsDefault ||
                    batch.Alerts.IsDefault || batch.Metrics.IsDefault)
                    throw new IngestionPublicationException("PUBLISHER_BATCH_INVALID");
                records += (long)batch.Objects.Length + batch.Relationships.Length + batch.Alerts.Length + batch.Metrics.Length;
                if (batch.WindowsInventory is { } windows)
                {
                    if (windows.Devices.IsDefault || windows.Volumes.IsDefault)
                        throw new IngestionPublicationException("PUBLISHER_BATCH_INVALID");
                    records += (long)windows.Devices.Length + windows.Volumes.Length;
                }

                if (records > settings.MaxRecords || sequence >= 10000)
                    throw new IngestionPublicationException("PUBLISHER_RECORD_BUDGET");
                var bytes = SerializeBounded(batch);
                totalBytes += bytes.Length;
                if (totalBytes > settings.MaxDatasetBytes)
                    throw new IngestionPublicationException("PUBLISHER_DATASET_BUDGET");
                await Send(route + "/batches/" + sequence, bytes, "accepted", true, deadline.Token);
                sequence++;
            }
            await Send(route + "/commit", manifestBytes, "datasetId", sessionId, deadline.Token);
            return sessionId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new IngestionPublicationException("PUBLISHER_DEADLINE");
        }
        finally { Volatile.Write(ref publishing, 0); }
    }

    public async Task BeginAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!ReadModelSettings.ValidId(sessionId) || Guid.Parse(sessionId).ToString("D") != sessionId)
            throw new IngestionPublicationException("PUBLISHER_SESSION_INVALID");
        if (Interlocked.CompareExchange(ref publishing, 1, 0) != 0)
            throw new IngestionPublicationException("PUBLISHER_ALREADY_RUNNING");
        try { await Send("ingestion/sessions/" + sessionId, null, "sessionId", sessionId, cancellationToken); }
        finally { Volatile.Write(ref publishing, 0); }
    }

    private byte[] SerializeBounded<T>(T value)
    {
        using var stream = new BoundedStream(settings.MaxBatchBytes);
        JsonSerializer.Serialize(stream, value, Wire.Json);
        return stream.ToArray();
    }

    private async Task Send(string route, byte[]? bytes, string property, object expected, CancellationToken token)
    {
        for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(8000, 250 * (1 << (attempt - 1))) + Random.Shared.Next(0, 100));
            try
            {
                var bearer = await acquireToken(requestDeadline.Token).WaitAsync(requestDeadline.Token);
                if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 32768 || bearer.Any(char.IsWhiteSpace))
                    throw new IngestionPublicationException("PUBLISHER_TOKEN_INVALID");
                using var request = new HttpRequestMessage(HttpMethod.Post, route);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (bytes is not null)
                {
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = new("application/json");
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    await VerifyResponse(response, property, expected, requestDeadline.Token);
                    return;
                }
                if (response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                    throw new IngestionPublicationException("PUBLISHER_HTTP_" + (int)response.StatusCode);
                var retry = response.Headers.RetryAfter;
                var serverDelay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                if (serverDelay is { } requested)
                {
                    if (requested > TimeSpan.FromSeconds(30))
                        throw new IngestionPublicationException("PUBLISHER_RETRY_DEFERRED");
                    if (requested > delay) delay = requested;
                }
            }
            catch (HttpRequestException)
            {
                // The server may already have acknowledged this exact request.
                // Retry the same session, sequence and bytes, never a new identity.
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            if (attempt == settings.MaxAttempts)
                throw new IngestionPublicationException("PUBLISHER_RETRY_EXHAUSTED");
            await Task.Delay(delay, token);
        }
    }

    private static async Task VerifyResponse(HttpResponseMessage response, string property, object expected, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > 4096)
            throw new IngestionPublicationException("PUBLISHER_RESPONSE_INVALID");
        using var buffer = new BoundedStream(4096);
        try { await response.Content.CopyToAsync(buffer, token); }
        catch (IngestionPublicationException)
        { throw new IngestionPublicationException("PUBLISHER_RESPONSE_INVALID"); }
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            SnapshotReader.RejectDuplicateProperties(json.RootElement);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty(property, out var value) ||
                (expected is string text ? value.ValueKind != JsonValueKind.String || value.GetString() != text :
                    value.ValueKind != JsonValueKind.True))
                throw new IngestionPublicationException("PUBLISHER_RESPONSE_INVALID");
        }
        catch (JsonException) { throw new IngestionPublicationException("PUBLISHER_RESPONSE_INVALID"); }
        catch (QueryFault) { throw new IngestionPublicationException("PUBLISHER_RESPONSE_INVALID"); }
    }

    public void Dispose() => client.Dispose();

    private sealed class BoundedStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Ensure(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Ensure(buffer.Length);
            base.Write(buffer);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Ensure(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Ensure(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
        private void Ensure(int count)
        {
            if (Length + count > limit)
                throw new IngestionPublicationException("PUBLISHER_PAYLOAD_BUDGET");
        }
    }
}
