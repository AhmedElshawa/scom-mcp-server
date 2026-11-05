using System.Security.Claims;

namespace ScomMcp;

public static class ReadModelHosting
{
    public static string RejectionReason(string reason) => reason switch
    {
        "SESSION_EXPIRED_EMPTY_RESERVATION" or "SESSION_REPLAY_OR_EXPIRED" or
        "COLLECTION_ALREADY_IN_PROGRESS" or "SESSION_ALREADY_CONSUMED" or
        "SESSION_LEDGER_LIMIT" or "SESSION_INVALID" => reason,
        _ => "INGESTION_REJECTED"
    };

    public static bool IsPublisher(ClaimsPrincipal principal, AuthSettings auth, ReadModelSettings settings)
    {
        var ids = principal.FindAll("oid").Select(c => c.Value).ToArray();
        return principal.Identity?.IsAuthenticated == true &&
            principal.FindFirst("tid")?.Value == auth.TenantId &&
            !principal.HasClaim(c => c.Type == "scp") &&
            principal.FindAll("tid").Count() == 1 &&
            ids.Length == 1 && Guid.TryParse(ids[0], out var id) &&
            settings.PublisherObjectIds.Contains(id.ToString("D")) &&
            principal.HasClaim("roles", settings.IngestionRole);
    }

    public static void Map(WebApplication app)
    {
        var settings = app.Services.GetRequiredService<ReadModelSettings>();
        if (!settings.Enabled) return;
        _ = app.Services.GetRequiredService<ReadModelStore>();
        var ingestion = app.MapGroup("/ingestion").RequireAuthorization("ScomIngest").RequireRateLimiting("ScomIngestion");
        ingestion.MapPost("/sessions/{sessionId}", (string sessionId, HttpContext context, ReadModelStore store) =>
            Handle(context, () => new { sessionId = store.Begin(Publisher(context), sessionId) }));
        ingestion.MapPost("/sessions/{sessionId}/batches/{sequence:int}",
            async (string sessionId, int sequence, HttpContext context, ReadModelStore store) =>
                await Body(context, settings, bytes =>
                {
                    store.Append(Publisher(context), sessionId, sequence, bytes);
                    return new { accepted = true };
                }));
        ingestion.MapPost("/sessions/{sessionId}/commit",
            async (string sessionId, HttpContext context, ReadModelStore store) =>
                await Body(context, settings, bytes => new { datasetId = store.Commit(Publisher(context), sessionId, bytes) }));
        ingestion.MapPost("/sessions/{sessionId}/fail", (string sessionId, HttpContext context, ReadModelStore store) =>
            Handle(context, () =>
            {
                store.Fail(Publisher(context), sessionId);
                return new { unavailable = true };
            }));
    }

    private static string Publisher(HttpContext context) => Guid.Parse(context.User.FindFirst("oid")!.Value).ToString("D");
    private static async Task<IResult> Body(HttpContext context, ReadModelSettings settings, Func<byte[], object> action)
    {
        if (context.Request.ContentLength > settings.MaxBatchBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestBodyTimeoutSeconds));
        var block = new byte[8192];
        int count;
        try
        {
            while ((count = await context.Request.Body.ReadAsync(block, deadline.Token)) != 0)
            {
                if (buffer.Length + count > settings.MaxBatchBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                buffer.Write(block, 0, count);
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILogger<ReadModelStore>>().LogWarning(
                "Ingestion body deadline exceeded. TraceId={TraceId}", context.TraceIdentifier);
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        return Handle(context, () => action(buffer.ToArray()));
    }
    private static IResult Handle(HttpContext context, Func<object> action)
    {
        try
        {
            var value = action();
            context.RequestServices.GetRequiredService<ILogger<ReadModelStore>>().LogInformation(
                "Ingestion accepted. Event={Event} Utc={Utc} TraceId={TraceId} Bytes={Bytes}",
                "SCOM_INGESTION_ACCEPTED", DateTimeOffset.UtcNow, context.TraceIdentifier, context.Request.ContentLength);
            return Results.Json(value, Wire.Json);
        }
        catch (QueryFault fault)
        {
            context.RequestServices.GetRequiredService<ILogger<ReadModelStore>>().LogWarning(
                "Ingestion rejected. Code={Code} Reason={Reason} TraceId={TraceId}",
                fault.Code, RejectionReason(fault.Message), context.TraceIdentifier);
            return Results.Json(new { error = fault.Code, reason = fault.Message }, statusCode: 409);
        }
    }
}
