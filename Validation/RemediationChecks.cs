using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ScomMcp;

// Checks for the F1-F3 remediation in docs/security-review-v0.2.md.
static class RemediationChecks
{
    private const string OtherOid = "00000000-0000-0000-0000-0000000000aa";
    private const string ThirdOid = "00000000-0000-0000-0000-0000000000bb";

    public static async Task Run(AuthSettings baseAuth, SourceSettings source, Dataset dataset, TimeProvider clock,
        Func<SecurityKey, string, string> token, Action<bool, string> check)
    {
        using var rsa = RSA.Create(2048);
        var signing = new RsaSecurityKey(rsa) { KeyId = "synthetic-remediation-key" };
        var auth = new AuthSettings
        {
            TenantId = baseAuth.TenantId, Audiences = baseAuth.Audiences, RequiredScope = baseAuth.RequiredScope,
            Callers = [.. baseAuth.Callers, new() { ObjectId = OtherOid, Scopes = baseAuth.Callers[0].Scopes },
                new() { ObjectId = ThirdOid, Scopes = baseAuth.Callers[0].Scopes }]
        };
        var primaryOid = baseAuth.Callers[0].ObjectId!;
        await RateLimit(auth, source, dataset, clock, signing, token, check, primaryOid);
        await Hsts(auth, source, dataset, clock, signing, check);
        await HttpsRedirect(auth, source, dataset, clock, signing, check);
        BridgeExecutable(check);
        CollectorAcl(check);
    }

    private static async Task<WebApplication> Start(AuthSettings auth, SourceSettings source, Dataset dataset, TimeProvider clock,
        SecurityKey signing, Action<WebApplicationBuilder>? configure = null, Action<WebApplication>? beforeMap = null,
        Action<WebApplication>? afterMap = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        Hosting.AddScomServices(builder.Services, auth, source);
        builder.Services.AddSingleton<ISnapshotReader>(new MemoryReader(dataset));
        builder.Services.AddSingleton(clock);
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Authority = null;
            o.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0"
            };
            o.Configuration.SigningKeys.Add(signing);
        });
        configure?.Invoke(builder);
        var app = builder.Build();
        beforeMap?.Invoke(app);
        Hosting.MapScom(app);
        afterMap?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static async Task<HttpStatusCode> Initialize(HttpClient client, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(Wire.Encode(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize", @params = new
                {
                    protocolVersion = "2025-03-26", capabilities = new { },
                    clientInfo = new { name = "synthetic-validation", version = "1" }
                }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task RateLimit(AuthSettings auth, SourceSettings source, Dataset dataset, TimeProvider clock,
        SecurityKey signing, Func<SecurityKey, string, string> token, Action<bool, string> check, string primaryOid)
    {
        var held = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await Start(auth, source, dataset, clock, signing, afterMap: web =>
            web.MapGet("/validation/hold", async () =>
            {
                held.Release();
                await release.Task;
                return "held";
            }).RequireAuthorization("ScomRead").RequireRateLimiting("ScomRead"));
        try
        {
            var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
            var mcp = endpoints.Where(e => e.RoutePattern.RawText?.TrimEnd('/') == "/mcp").ToArray();
            check(mcp.Length > 0 && mcp.All(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == "ScomRead"),
                "F1 every /mcp endpoint carries the ScomRead rate-limit policy");
            var health = endpoints.Single(e => e.RoutePattern.RawText == "/healthz");
            check(health.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null, "F1 /healthz has no rate-limit policy");

            using var client = new HttpClient { BaseAddress = new Uri(Address(app)), Timeout = TimeSpan.FromSeconds(30) };
            var first = token(signing, primaryOid);
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 30; i++) statuses.Add(await Initialize(client, first));
            check(statuses.All(s => s == HttpStatusCode.OK), "F1 first 30 /mcp requests per minute are admitted");
            check(await Initialize(client, first) == HttpStatusCode.TooManyRequests, "F1 request 31 in the window gets HTTP 429");
            check(await Initialize(client, token(signing, OtherOid)) == HttpStatusCode.OK,
                "F1 limit is per oid: a different caller is unaffected");
            using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
            var anonymousStatuses = new List<HttpStatusCode>();
            for (var i = 0; i < 60; i++) anonymousStatuses.Add((await anonymous.GetAsync("/healthz")).StatusCode);
            check(anonymousStatuses.All(s => s == HttpStatusCode.OK), "F1 anonymous /healthz is never rate limited");

            var heldRequests = Enumerable.Range(0, 4).Select(_ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/validation/hold");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token(signing, ThirdOid));
                return client.SendAsync(request);
            }).ToArray();
            try
            {
                for (var i = 0; i < 4; i++) check(await held.WaitAsync(TimeSpan.FromSeconds(10)), "F1 four concurrent requests are admitted");
                using var fifth = new HttpRequestMessage(HttpMethod.Get, "/validation/hold");
                fifth.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token(signing, ThirdOid));
                using var rejected = await client.SendAsync(fifth);
                check(rejected.StatusCode == HttpStatusCode.TooManyRequests, "F1 fifth concurrent request is rejected with no queue");
            }
            finally { release.TrySetResult(); }
            foreach (var request in heldRequests) { using var done = await request; check(done.IsSuccessStatusCode, "F1 admitted request completes"); }
            check(await Initialize(client, token(signing, ThirdOid)) == HttpStatusCode.OK,
                "F1 concurrency slot is released after completion");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task Hsts(AuthSettings auth, SourceSettings source, Dataset dataset, TimeProvider clock,
        SecurityKey signing, Action<bool, string> check)
    {
        // The harness listens on http, so mark requests as https the way a TLS-terminating front end would.
        await using var app = await Start(auth, source, dataset, clock, signing, beforeMap: web =>
            web.Use((context, next) => { context.Request.Scheme = "https"; return next(context); }));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = "scom.example.invalid";
        using var response = await client.SendAsync(request);
        check(response.IsSuccessStatusCode && response.Headers.TryGetValues("Strict-Transport-Security", out var values) &&
            values.Single() == "max-age=31536000", "F2 non-development HTTPS responses send HSTS with a one-year max-age");
    }

    private static async Task HttpsRedirect(AuthSettings auth, SourceSettings source, Dataset dataset, TimeProvider clock,
        SecurityKey signing, Action<bool, string> check)
    {
        await using var app = await Start(auth, source, dataset, clock, signing,
            configure: builder => builder.Configuration["HTTPS_PORT"] = "443");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(Address(app)) };
        using var response = await client.GetAsync("/healthz");
        check(response.StatusCode == HttpStatusCode.TemporaryRedirect && response.Headers.Location?.Scheme == "https",
            "F2 non-development plain HTTP is redirected to HTTPS when an HTTPS port is configured");
    }

    private static void BridgeExecutable(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var executable in new[] { "powershell.exe", @"..\powershell.exe",
            Path.Combine(Path.GetTempPath(), "scom-missing-" + Guid.NewGuid().ToString("N"), "powershell.exe") })
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var refused = false;
            try
            {
                NormalizationProcess.RunAsync(start, Encoding.UTF8.GetBytes("{}"), 4096, TimeSpan.FromSeconds(5),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (QueryFault fault) { refused = fault.Code == "SOURCE_UNAVAILABLE" && fault.Message == "SOURCE_BRIDGE_EXECUTABLE_PATH_REQUIRED"; }
            check(refused, "F3 bridge refuses a relative or missing executable instead of searching PATH: " + executable);
        }
    }

    private static void CollectorAcl(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        DirectorySecurity Protected(params (SecurityIdentifier Sid, FileSystemRights Rights, AccessControlType Type)[] rules)
        {
            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (var rule in rules)
                security.AddAccessRule(new FileSystemAccessRule(rule.Sid, rule.Rights, InheritanceFlags.ContainerInherit |
                    InheritanceFlags.ObjectInherit, PropagationFlags.None, rule.Type));
            return security;
        }

        var installed = Protected((system, FileSystemRights.FullControl, AccessControlType.Allow),
            (administrators, FileSystemRights.FullControl, AccessControlType.Allow),
            (users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        check(CollectorDirectoryPolicy.FindViolation(installed) is null,
            "F3 installed layout (SYSTEM and Administrators full control, others read-only) is accepted");
        foreach (var right in new[] { FileSystemRights.Modify, FileSystemRights.WriteData, FileSystemRights.AppendData,
            FileSystemRights.Delete, FileSystemRights.DeleteSubdirectoriesAndFiles, FileSystemRights.ChangePermissions,
            FileSystemRights.TakeOwnership, FileSystemRights.FullControl })
        {
            var writable = Protected((system, FileSystemRights.FullControl, AccessControlType.Allow),
                (administrators, FileSystemRights.FullControl, AccessControlType.Allow), (users, right, AccessControlType.Allow));
            check(CollectorDirectoryPolicy.FindViolation(writable) == users.Value, "F3 other principals may not hold " + right);
        }
        var denied = Protected((system, FileSystemRights.FullControl, AccessControlType.Allow),
            (users, FileSystemRights.Modify, AccessControlType.Deny));
        check(CollectorDirectoryPolicy.FindViolation(denied) is null, "F3 deny entries do not count as write grants");
        var wrongOwner = Protected((system, FileSystemRights.FullControl, AccessControlType.Allow));
        wrongOwner.SetOwner(users);
        check(CollectorDirectoryPolicy.FindViolation(wrongOwner) == "owner", "F3 owner must be SYSTEM or Administrators");

        // End to end on the real file system: a user-writable directory must be refused.
        var directory = Path.Combine(Path.GetTempPath(), "scom-collector-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "collector.ps1");
            File.WriteAllText(script, "# synthetic");
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            var rejected = false;
            var previous = CollectorPolicySettings.Enforce;
            CollectorPolicySettings.Enforce = true;
            try { CollectorDirectoryPolicy.Require(script); }
            catch (InvalidOperationException) { rejected = true; }
            finally { CollectorPolicySettings.Enforce = previous; }
            check(rejected, "F3 a collector directory writable by Everyone is refused");
        }
        finally { Directory.Delete(directory, true); }
    }
}
