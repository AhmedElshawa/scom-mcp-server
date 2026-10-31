using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;

namespace ScomMcp;

public static class Hosting
{
    public static void AddScomServices(IServiceCollection services, AuthSettings auth, SourceSettings source)
    {
        auth.Validate();
        source.Validate();
        services.AddSingleton(auth);
        services.AddSingleton(source);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISnapshotReader, SnapshotReader>();
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
        });
        services.AddMcpServer(options => options.ServerInfo = new() { Name = "ScomReadOnlyMcp", Version = "1.0.0" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<ScomReadOnlyTools>();
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
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "REQUEST_FAILED" });
        }));
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
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
    }
}
