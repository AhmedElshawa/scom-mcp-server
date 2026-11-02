using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ScomMcp;

public static class Hosting
{
    public static void AddScomServices(IServiceCollection services, AuthSettings auth, SourceSettings source,
        ReadModelSettings? readModel = null, LifecycleSettings? lifecycle = null, TargetedLiveSettings? targetedLive = null)
    {
        auth.Validate();
        source.Validate();
        lifecycle ??= new();
        lifecycle.Validate();
        readModel ??= new();
        readModel.Validate();
        targetedLive ??= new();
        targetedLive.Validate(readModel);
        if (!source.SnapshotEnabled && !readModel.Enabled)
            throw new InvalidOperationException("At least one source must be enabled.");
        if (readModel.Enabled && auth.Callers.Any(c => c.ObjectId is null))
            throw new InvalidOperationException("Read model callers require immutable object-ID grants.");
        if (readModel.WindowsToolsEnabled && (!readModel.Enabled || source.SnapshotEnabled ||
            source.FreshMinutes != readModel.FreshMinutes || source.UnavailableMinutes != readModel.UnavailableMinutes))
            throw new InvalidOperationException("Indexed Windows tools require read model enabled, legacy snapshots disabled and matching freshness settings.");
        services.AddSingleton(auth);
        services.AddSingleton(source);
        services.AddSingleton(lifecycle);
        services.AddSingleton(targetedLive);
        services.AddSingleton<TargetedLiveChecks>();
        services.AddSingleton(readModel);
        services.AddSingleton<ReadModelStore>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter("ScomIngestion", limiter =>
            {
                limiter.PermitLimit = 1;
                limiter.QueueLimit = 0;
            });
        });
        services.AddSingleton(TimeProvider.System);
        if (readModel.WindowsToolsEnabled) services.AddSingleton<ISnapshotReader, IndexedWindowsReader>();
        else services.AddSingleton<ISnapshotReader, SnapshotReader>();
        services.AddSingleton<CursorCodec>();
        services.AddSingleton<QueryEngine>();
        services.AddHttpContextAccessor();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0";
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = false;
            options.TokenValidationParameters = TokenValidation(auth);
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("ScomRead", policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => CallerAuthorization.Resolve(context.User, auth) is not null));
            options.AddPolicy("ScomIngest", policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => ReadModelHosting.IsPublisher(context.User, auth, readModel)));
        });
        var mcp = services.AddMcpServer(options => options.ServerInfo = new() { Name = "ScomReadOnlyMcp", Version = "1.0.0" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<ScomReadOnlyTools>()
            .WithTools<TargetedLiveTools>();
        if (readModel.Enabled) mcp.WithTools<ReadModelTools>();
    }

    public static TokenValidationParameters TokenValidation(AuthSettings auth) => new()
    {
        ValidateIssuer = true,
        ValidIssuers =
        [
            $"https://login.microsoftonline.com/{auth.TenantId}/v2.0",
            $"https://sts.windows.net/{auth.TenantId}/"
        ],
        ValidateAudience = true,
        ValidAudiences = auth.Audiences,
        IgnoreTrailingSlashWhenValidatingAudience = false,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    public static void MapScom(WebApplication app)
    {
        app.UseExceptionHandler(error => error.Run(async context =>
        {
            var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var sqlite = failure as SqliteException;
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ScomRequest").LogError(
                "Request failed. TraceId={TraceId} ErrorType={ErrorType} SqliteErrorCode={SqliteErrorCode} SqliteExtendedErrorCode={SqliteExtendedErrorCode}",
                context.TraceIdentifier, failure?.GetType().Name, sqlite?.SqliteErrorCode, sqlite?.SqliteExtendedErrorCode);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "REQUEST_FAILED" });
        }));
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") && HttpMethods.IsPost(context.Request.Method))
            {
                if (context.Request.ContentLength > 32768)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                using var buffer = new MemoryStream();
                var block = new byte[4096];
                int read;
                while ((read = await context.Request.Body.ReadAsync(block, context.RequestAborted)) != 0)
                {
                    if (buffer.Length + read > 32768)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return;
                    }
                    buffer.Write(block, 0, read);
                }
                buffer.Position = 0;
                try
                {
                    using var json = JsonDocument.Parse(buffer, new JsonDocumentOptions { MaxDepth = 32 });
                    var root = json.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String &&
                        method.GetString() == "tools/call" && root.TryGetProperty("params", out var parameters) &&
                        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var arguments) &&
                        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("scopeId", out var scope) &&
                        scope.ValueKind == JsonValueKind.String)
                    {
                        var auth = context.RequestServices.GetRequiredService<AuthSettings>();
                        var caller = CallerAuthorization.Resolve(context.User, auth);
                        if (caller is null) throw new QueryFault("FORBIDDEN", "Scope is not authorized.");
                        CallerAuthorization.AuthorizeScope(caller, scope.GetString());
                    }
                }
                catch (QueryFault)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "FORBIDDEN" });
                    return;
                }
                catch (JsonException) { /* The SDK returns the JSON-RPC parse error. */ }
                buffer.Position = 0;
                context.Request.Body = buffer;
                await next(context);
                return;
            }
            await next(context);
        });
        app.MapGet("/healthz", () => Results.Json(new { service = "ScomReadOnlyMcp", version = "1.0.0" })).AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization("ScomRead");
        ReadModelHosting.Map(app);
        TargetedLiveHosting.Map(app);
    }
}
