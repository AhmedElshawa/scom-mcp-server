using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScomMcp;

public sealed class LiveWorkerSettings
{
    public bool Enabled { get; set; }
    public bool QueryContractVerified { get; set; }
    public LiveSourceBinding Source { get; set; } = new("", "group", null);
    public PowerShellProfileSettings Profile { get; set; } = new();
    public int PollSeconds { get; set; } = 2;

    public void Validate()
    {
        if (PollSeconds is < 2 or > 5 || Enabled && (!QueryContractVerified || Source is null ||
            !ReadModelSettings.ValidId(Source.ManagementGroupId) || Source.ScopeKind is not ("group" or "managementGroup") ||
            (Source.ScopeKind == "group" ? !ReadModelSettings.ValidId(Source.GroupId) : Source.GroupId is not null) ||
            Profile is null || Profile.DeadlineSeconds is < 1 or > 10 || Profile.MaxOutputBytes is < 1024 or > 32768))
            throw new InvalidOperationException("Live worker requires verified bounded source contracts, source pins and a dedicated bounded profile.");
    }

    public static string Policy(PowerShellProfileSettings profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Encode(new { profile.ScriptSha256, profile.Configuration }))));
}

public interface ITargetedLiveSource
{
    // Must freshly verify the configured source scope before reading this one object; no retained authorization or global fallback.
    Task<LiveCheckObservation> VerifyAsync(LiveCheckRequest request, CancellationToken cancellationToken);
}

internal sealed class PowerShellTargetedLiveSource(PowerShellNormalizationProfile profile) : ITargetedLiveSource
{
    public Task<LiveCheckObservation> VerifyAsync(LiveCheckRequest request, CancellationToken cancellationToken) =>
        profile.VerifyAsync(request, cancellationToken);
}

public sealed class TargetedLiveWorker : IDisposable
{
    private readonly HttpClient client;
    private readonly Func<CancellationToken, Task<string>> token;
    private readonly LiveClaim claim;
    private readonly ITargetedLiveSource source;
    private readonly TimeProvider clock;
    private readonly ILogger<TargetedLiveWorker> logger;
    private int running;

    public TargetedLiveWorker(IngestionPublisherSettings publisher, Func<CancellationToken, Task<string>> token,
        LiveClaim claim, ITargetedLiveSource source, TimeProvider clock, ILogger<TargetedLiveWorker> logger)
        : this(publisher, token, claim, source, clock, logger, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    public TargetedLiveWorker(IngestionPublisherSettings publisher, Func<CancellationToken, Task<string>> token,
        LiveClaim claim, ITargetedLiveSource source, TimeProvider clock, ILogger<TargetedLiveWorker> logger, HttpMessageHandler handler)
    {
        publisher.Validate();
        if (!claim.ContractVerified || !TargetedLiveSettings.Hash(claim.ProviderPolicySha256))
            throw new InvalidOperationException("Live worker provider contract is unverified.");
        var uri = publisher.Endpoint.AbsoluteUri;
        client = new(handler) { BaseAddress = new Uri(uri.EndsWith("/", StringComparison.Ordinal) ? uri : uri + "/"), Timeout = Timeout.InfiniteTimeSpan };
        this.token = token;
        this.claim = claim;
        this.source = source;
        this.clock = clock;
        this.logger = logger;
    }

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw TargetedLiveChecks.Fault("LIVE_WORKER_ALREADY_RUNNING");
        try
        {
            var bytes = await Post("ingestion/live/claim", claim, 2048, cancellationToken);
            using var json = JsonDocument.Parse(bytes);
            SnapshotReader.RejectDuplicateProperties(json.RootElement);
            if (!json.RootElement.TryGetProperty("request", out var value)) throw TargetedLiveChecks.Fault("LIVE_TRANSPORT_PROTOCOL");
            if (value.ValueKind == JsonValueKind.Null) return false;
            var request = value.Deserialize<LiveCheckRequest>(Wire.Json);
            if (request is null || request.Source != claim.Source || !ReadModelSettings.ValidId(request.RequestId) ||
                !ReadModelSettings.ValidId(request.ObjectId) || request.ExecutionSeconds is < 1 or > 10 ||
                request.MaxAlerts is < 1 or > 100 || request.MaxSourceRequests is < 1 or > 3 ||
                request.RequestedAtUtc > clock.GetUtcNow() || request.ExpiresAtUtc <= clock.GetUtcNow() ||
                request.ExpiresAtUtc - request.RequestedAtUtc > TimeSpan.FromSeconds(30))
                throw TargetedLiveChecks.Fault("LIVE_TRANSPORT_PROTOCOL");
            LiveCheckObservation observation;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var execution = TimeSpan.FromSeconds(request.ExecutionSeconds);
            var remaining = request.ExpiresAtUtc - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) deadline.Cancel();
            else deadline.CancelAfter(remaining < execution ? remaining : execution);
            try
            {
                // Await cleanup even for cancellation. Production profile uses a kill-on-close child job and joins descendants.
                deadline.Token.ThrowIfCancellationRequested();
                observation = await source.VerifyAsync(request, deadline.Token);
                if (deadline.IsCancellationRequested) throw new OperationCanceledException(deadline.Token);
            }
            catch (Exception error) when (error is QueryFault or OperationCanceledException or IOException)
            {
                logger.LogWarning("Targeted source unavailable. RequestId={RequestId} ErrorType={ErrorType}",
                    request.RequestId, error.GetType().Name);
                observation = new(request.RequestId, request.ObjectId, request.Source, "unavailable", false, default,
                    clock.GetUtcNow(), null, null, false, [], 0);
            }
            // Ambiguous result delivery replays identical content once, never re-runs the source.
            byte[] acknowledgement;
            try { acknowledgement = await Post("ingestion/live/result", new LiveCompletion(observation), 1024, cancellationToken); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException)
            {
                logger.LogWarning("Targeted result delivery ambiguous; retrying same result. RequestId={RequestId}", request.RequestId);
                acknowledgement = await Post("ingestion/live/result", new LiveCompletion(observation), 1024, cancellationToken);
            }
            using var accepted = JsonDocument.Parse(acknowledgement);
            SnapshotReader.RejectDuplicateProperties(accepted.RootElement);
            if (!accepted.RootElement.TryGetProperty("accepted", out var flag) || flag.ValueKind != JsonValueKind.True)
                throw TargetedLiveChecks.Fault("LIVE_TRANSPORT_PROTOCOL");
            return true;
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task<byte[]> Post<T>(string path, T value, int maxResponse, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
        if (body.Length > 32768) throw TargetedLiveChecks.Fault("LIVE_TRANSPORT_BUDGET");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(deadline.Token));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw TargetedLiveChecks.Fault("LIVE_TRANSPORT_REJECTED");
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (output.Length + read > maxResponse) throw TargetedLiveChecks.Fault("LIVE_TRANSPORT_BUDGET");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose() => client.Dispose();
}
