using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ScomMcp;

if (args is ["--targeted-live-checks"])
{
    await TargetedLiveValidation.Run();
    return 0;
}
if (args is ["--lifecycle-checks"])
{
    OsLifecycleChecks.Run();
    return 0;
}
if (args is ["--read-model-fault", var mode, var path, var session])
    return ReadModelFaultChecks.Child(mode, path, session);
if (args is ["--normalization-child", var normalizationMode])
    return NormalizationProcessChecks.Child(normalizationMode);
if (args.Length != 0)
    return await CapturedSnapshot.Run(args, TimeProvider.System, Console.Out, Console.Error);
await Checks.Run();
return 0;

static class Checks
{
    private static int passed;
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 13, 30, 0, TimeSpan.Zero);
    private const string A = "00000000-0000-0000-0000-000000000001";
    private const string B = "00000000-0000-0000-0000-000000000002";
    private const string V1 = "00000000-0000-0000-0000-000000000011";
    private const string V2 = "00000000-0000-0000-0000-000000000012";
    private const string V3 = "00000000-0000-0000-0000-000000000013";
    private const string Oid = "00000000-0000-0000-0000-000000000099";
    private const string SyntheticTenant = "11111111-1111-4111-8111-111111111111";
    private static readonly ManualClock Clock = new(Now);
    private static readonly Caller Caller = new("oid:" + Oid, ImmutableHashSet.Create(Wire.ScopeId));

    public static async Task Run()
    {
        var fixtureRoot = Path.Combine(FindRoot(), ".validation-data");
        var root = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Source(root);
            var auth = Auth();
            source.Validate();
            auth.Validate();
            TestAuth(auth);
            SetupConfigurationChecks.Run(root);
            var snapshot = Fixture();
            var dataset = Validate(snapshot, source);
            var codec = new CursorCodec(Clock, source);
            var engine = new QueryEngine(source, Clock, codec);
            TestQueries(dataset, source, codec, engine);
            ObservationLookupChecks.Run(dataset, source, Clock);
            OsLifecycleChecks.Run();
            await TargetedLiveValidation.Run();
            TestNativeMemory(snapshot, source, engine);
            TestOptimizationMetadata(snapshot, source, engine);
            TestValidation(snapshot, source, engine);
            await TestReader(root, snapshot, source);
            await TestCapturedSnapshot(root, snapshot, source);
            await TestHttp(auth, source, dataset);
            await ReadModelChecks.Run(root);
            await ReadModelFaultChecks.Run(root);
            await IngestionPublisherChecks.Run(root);
            await CollectionSchedulerChecks.Run();
            await IndexedWindowsChecks.Run(root, dataset);
            AlertReconcilerChecks.Run();
            await CollectionWorkerChecks.Run();
            MetricReconcilerChecks.Run();
            await ReconciledSourceChecks.Run(root);
            await SourceCheckpointChecks.Run(root);
            await NormalizationProcessChecks.Run();
            await CertificatePublisherChecks.Run();
            await CollectionWorkerHostChecks.Run(root);
            WorkerRuntimeStateChecks.Run(root);
            await WindowsSourceProjectionChecks.Run(root);
            await ManagementGroupSourceChecks.Run(root);
            Console.WriteLine($"PASS: {passed} assertions; pure auth, snapshots, counts, cursors, math, and authenticated MCP HTTP verified.");
        }
        finally
        {
            Directory.Delete(root, true);
            try { Directory.Delete(fixtureRoot, false); }
            catch (IOException) { /* Another validation process may own a sibling fixture directory. */ }
        }
    }

    private static void TestAuth(AuthSettings auth)
    {
        Throws<InvalidOperationException>(() => new AuthSettings().Validate(), "missing auth fails closed");
        var noAudience = Auth(); noAudience.Audiences = [];
        Throws<InvalidOperationException>(noAudience.Validate, "missing audience fails closed");
        var noScope = Auth(); noScope.RequiredScope = "";
        Throws<InvalidOperationException>(noScope.Validate, "missing required scope fails closed");
        var noCallers = Auth(); noCallers.Callers = [];
        Throws<InvalidOperationException>(noCallers.Validate, "missing caller map fails closed");
        var wildcard = Auth(); wildcard.Audiences = ["*"];
        Throws<InvalidOperationException>(wildcard.Validate, "wildcard audience fails closed");
        var tenant = Auth(); tenant.TenantId = Guid.Empty.ToString();
        Throws<InvalidOperationException>(tenant.Validate, "empty tenant fails closed");
        var invalidTenant = Auth(); invalidTenant.TenantId = "not-a-guid";
        Throws<InvalidOperationException>(invalidTenant.Validate, "invalid tenant fails closed");
        var alternateTenant = Auth(); alternateTenant.TenantId = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
        alternateTenant.Validate();
        Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", alternateTenant.TenantId, "configured tenant is normalized");
        Check(CallerAuthorization.Resolve(Principal(tid: alternateTenant.TenantId), alternateTenant) is not null,
            "another explicitly configured tenant can authorize its caller");
        Equal(null, CallerAuthorization.Resolve(Principal(), alternateTenant), "other tenant cannot use configured grant");
        Equal(null, CallerAuthorization.Resolve(new ClaimsPrincipal(new ClaimsIdentity()), auth), "anonymous denied");
        Equal(null, CallerAuthorization.Resolve(Principal(scope: "other"), auth), "missing delegated scope denied");
        Equal(null, CallerAuthorization.Resolve(Principal(oid: Guid.Empty.ToString()), auth), "unmapped oid denied");
        Equal(null, CallerAuthorization.Resolve(Principal(tid: Guid.Empty.ToString()), auth), "wrong tenant denied");
        Check(CallerAuthorization.Resolve(Principal(), auth) is not null, "oid grant accepted");
        var alternateCaller = Auth();
        alternateCaller.Callers[0].ObjectId = Guid.Parse(alternateCaller.Callers[0].ObjectId!).ToString("B");
        alternateCaller.Validate();
        Check(CallerAuthorization.Resolve(Principal(), alternateCaller) is not null, "noncanonical configured caller GUID normalized");
        var emptyCaller = Auth();
        emptyCaller.Callers[0].ObjectId = Guid.Empty.ToString("D");
        Throws<InvalidOperationException>(emptyCaller.Validate, "empty caller GUID refused");
        Fault("FORBIDDEN", () => CallerAuthorization.AuthorizeScope(Caller, "unknown-scope"), "unknown scope denied");
        Fault("FORBIDDEN", () => CallerAuthorization.AuthorizeScope(new("other", ImmutableHashSet<string>.Empty), null), "scope grant required");
        var upnAuth = Auth();
        upnAuth.Callers = [new() { UserPrincipalName = "synthetic@example.invalid", Scopes = [Wire.ScopeId] }];
        var upn = Principal(oid: Guid.Empty.ToString(), extras: [new("upn", "SYNTHETIC@example.invalid")]);
        Check(CallerAuthorization.Resolve(upn, upnAuth) is not null, "UPN fallback explicit grant");
        foreach (var field in new[] { "", "\"ObjectId\":null,", "\"ObjectId\":\"\",", "\"ObjectId\":\" \"," })
        {
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"Auth\":{\"Callers\":[{" + field +
                "\"UserPrincipalName\":\"synthetic@example.invalid\",\"Scopes\":[\"scom-lab\"]}]}}"));
            var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
            var legacyGrant = Auth();
            legacyGrant.Callers = [];
            configuration.GetSection("Auth").Bind(legacyGrant);
            legacyGrant.Validate();
            Equal(null, legacyGrant.Callers[0].ObjectId, "blank optional object ID normalizes to null");
            Check(CallerAuthorization.Resolve(upn, legacyGrant) is not null, "provider-bound legacy UPN grant accepted");
            Equal(null, CallerAuthorization.Resolve(Principal(extras: [new("upn", "denied@example.invalid")]), legacyGrant),
                "provider-bound legacy grant denies another UPN");
            Throws<InvalidOperationException>(() => Hosting.AddScomServices(new ServiceCollection(), legacyGrant,
                new SourceSettings { SnapshotEnabled = false }, new ReadModelSettings
                {
                    Enabled = true, DatabasePath = Path.GetFullPath("synthetic-unused.db"),
                    ManagementGroupId = "22222222-2222-4222-8222-222222222222",
                    ScopeKind = "managementGroup", PublisherObjectIds = ["aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"]
                }), "indexed mode still rejects provider-bound UPN grants");
        }
        var ambiguous = Principal(extras: [new("upn", "synthetic@example.invalid"), new("preferred_username", "other@example.invalid")]);
        Equal(null, CallerAuthorization.Resolve(ambiguous, upnAuth), "ambiguous identity denied");
        Check(Hosting.TokenValidation(auth).ValidateIssuer && Hosting.TokenValidation(auth).ValidateAudience &&
            Hosting.TokenValidation(auth).ValidateLifetime && Hosting.TokenValidation(auth).RequireSignedTokens,
            "mandatory token validation settings");
        var delegated = Auth(); delegated.RequiredScope = "access_as_user";
        Check(CallerAuthorization.Resolve(Principal(scope: "access_as_user"), delegated) is not null,
            "approved access_as_user delegated scope with caller grant accepted");
        Equal(null, CallerAuthorization.Resolve(Principal(scope: "access_as_app"), delegated),
            "differently named delegated scope does not authorize access_as_user");
        Equal(null, CallerAuthorization.Resolve(Principal(scope: "api://synthetic-scom-validation/.default"), delegated),
            "OAuth default request scope is not an accepted delegated scp claim");
    }

    private static void TestQueries(Dataset ds, SourceSettings source, CursorCodec codec, QueryEngine engine)
    {
        Equal(2, ds.Snapshot.Devices.Length, "duplicate computers collapsed");
        Equal(1, ds.DuplicateDevices, "duplicate resolution reported");
        Equal(3, ds.Snapshot.Volumes.Length, "volumes retained separately");
        Equal(1, ds.DuplicateSamples, "exact duplicate observations collapsed");
        Equal(2, Data(engine.Inventory(ds, new())).GetProperty("uniqueComputerCount").GetInt32(), "machine count excludes volumes");
        Equal(1, Data(engine.Inventory(ds, new(Health: "CRITICAL"))).GetProperty("uniqueComputerCount").GetInt32(), "exact health filter");
        var unsupported = engine.Inventory(ds, new(EntityKind: "networkDevice"));
        Equal(0, Data(unsupported).GetProperty("uniqueComputerCount").GetInt32(), "network count not fabricated");
        Equal("partial", unsupported.Status, "network unsupported limitation");
        Fault("INVALID_ARGUMENT", () => engine.Inventory(ds, new(Health: "maybe")), "invalid filter rejected");

        var first = Data(engine.ListDevices(ds, Caller, Wire.ScopeId, new(), 1, null));
        Equal(2, first.GetProperty("totalCount").GetInt32(), "paging total full dataset");
        Equal(A, first.GetProperty("rows")[0].GetProperty("entityId").GetString(), "page stable hostname order");
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = Data(engine.ListDevices(ds, Caller, Wire.ScopeId, new(), 1, cursor));
        Equal(B, second.GetProperty("rows")[0].GetProperty("entityId").GetString(), "second distinct machine");
        Check(second.GetProperty("nextCursor").ValueKind == JsonValueKind.Null, "terminal cursor null");
        Fault("INVALID_CURSOR", () => engine.ListDevices(ds, Caller, Wire.ScopeId, new(), 1, "!" + cursor), "tampering rejected");
        Fault("INVALID_CURSOR", () => engine.ListDevices(ds, Caller with { Binding = "other" }, Wire.ScopeId, new(), 1, cursor), "cross-caller replay rejected");
        Fault("INVALID_CURSOR", () => engine.ListDevices(ds, Caller, Wire.ScopeId, new(Health: "healthy"), 1, cursor), "filter replay rejected");
        Fault("INVALID_CURSOR", () => engine.ListDevices(ds, Caller, Wire.ScopeId, new(), 2, cursor), "page-size replay rejected");
        Fault("CURSOR_DATASET_CHANGED", () => engine.ListDevices(ds with { Snapshot = ds.Snapshot with { DatasetId = "new" } },
            Caller, Wire.ScopeId, new(), 1, cursor), "dataset change explicit");
        Fault("CURSOR_DATASET_CHANGED", () => engine.ListDevices(ds with { Fingerprint = "changed" },
            Caller, Wire.ScopeId, new(), 1, cursor), "same dataset ID altered content explicit");
        Clock.Now = Now.AddMinutes(6);
        Fault("INVALID_CURSOR", () => engine.ListDevices(ds, Caller, Wire.ScopeId, new(), 1, cursor), "cursor expiry");
        Clock.Now = Now;
        Fault("INVALID_ARGUMENT", () => engine.ListDevices(ds, Caller, Wire.ScopeId, new(), 51, null), "page size bounded");
        Fault("NOT_FOUND", () => engine.Details(ds, Guid.Empty.ToString()), "unknown and unauthorized same not found");
        Equal(2, Data(engine.Details(ds, A)).GetProperty("volumes").GetArrayLength(), "device details only own volumes");

        var cpu = Data(engine.Utilization(ds, "cpuPercent", null, "1h", "mean", 1, true, "desc", 25, "gt"));
        Equal(2, cpu.GetProperty("consideredCount").GetInt32(), "util considered before top");
        Equal(2, cpu.GetProperty("matchedCount").GetInt32(), "threshold matched before top");
        Equal(1, cpu.GetProperty("rows").GetArrayLength(), "top truncation only rows");
        Equal(B, cpu.GetProperty("rows")[0].GetProperty("entityId").GetString(), "highest mean first");
        Near(90, cpu.GetProperty("rows")[0].GetProperty("value").GetDouble(), "CPU latest mean B");
        Near(80, cpu.GetProperty("rows")[0].GetProperty("previousValue").GetDouble(), "previous same-size window");
        Near(10, cpu.GetProperty("rows")[0].GetProperty("changePercentagePoints").GetDouble(), "percentage-point change");
        var a = Data(engine.Utilization(ds, "cpuPercent", A, "1h", "mean", 20, true, null, null, null)).GetProperty("rows")[0];
        Near(30, a.GetProperty("value").GetDouble(), "arithmetic observation mean");
        Near(10, a.GetProperty("previousValue").GetDouble(), "previous A not zero");
        Equal(2, a.GetProperty("coverage").GetProperty("sampleCount").GetInt32(), "sample count exact");
        Equal(false, a.GetProperty("coverage").GetProperty("fullWindowCovered").GetBoolean(), "optimized data never claims full-window coverage");
        var max = Data(engine.Utilization(ds, "cpuPercent", A, "1h", "max", 20, false, null, null, null));
        Near(40, max.GetProperty("rows")[0].GetProperty("value").GetDouble(), "maximum observation");
        var strict = Data(engine.Utilization(ds, "cpuPercent", null, "1h", "latest", 20, false, "asc", 40, "lt"));
        Equal(0, strict.GetProperty("matchedCount").GetInt32(), "threshold comparison strictly less");
        var memory = Data(engine.Utilization(ds, "memoryUsedPercent", null, "1h", "latest", 20, true, null, null, null));
        Equal(1, memory.GetProperty("missingCount").GetInt32(), "missing memory total not zero");
        Near(75, memory.GetProperty("rows")[0].GetProperty("value").GetDouble(), "memory used derived correctly");
        Check(memory.GetProperty("rows")[0].GetProperty("previousValue").ValueKind == JsonValueKind.Null, "missing previous remains null");
        var disks = Data(engine.Utilization(ds, "diskFreePercent", A, "1h", "latest", 20, false, "asc", null, null));
        Equal(2, disks.GetProperty("rows").GetArrayLength(), "disk per volume not machine aggregate");
        Equal(A, disks.GetProperty("rows")[0].GetProperty("parentEntityId").GetString(), "disk parent returned");
        Near(10, disks.GetProperty("rows")[0].GetProperty("value").GetDouble(), "ascending disk free");
        Fault("SOURCE_UNAVAILABLE", () => engine.Utilization(ds, "diskFreePercent", V3, "1h", "latest", 1, false, null, null, null), "all metrics missing explicit unavailable");
        Fault("INVALID_ARGUMENT", () => engine.Utilization(ds, "cpuPercent", null, "30d", "latest", 1, false, null, null, null), "unsupported window rejected");
        Fault("INVALID_ARGUMENT", () => engine.Utilization(ds, "cpuPercent", null, "1h", "latest", 21, false, null, null, null), "top bounded");
        Fault("INVALID_ARGUMENT", () => engine.Utilization(ds, "cpuPercent", null, "1h", "latest", 1, false, null, double.NaN, "gt"), "nonfinite input rejected");

        var cap = Data(engine.Capacity(ds, Caller, Wire.ScopeId, null, "disk", null, 0, 20, 20, null));
        Equal(3, cap.GetProperty("consideredRowCount").GetInt32(), "capacity volume denominator");
        Equal(1, cap.GetProperty("matchingVolumeCount").GetInt32(), "matching volume count");
        Equal(1, cap.GetProperty("distinctMachineCount").GetInt32(), "matching machine denominator");
        Equal(1, cap.GetProperty("unknownUsedOrFreeCount").GetInt32(), "unknown usage not zero");
        Near(1000, cap.GetProperty("comparableCapacity").GetProperty("freeMiB").GetDouble(), "disk free MiB");
        Near(9000, cap.GetProperty("comparableCapacity").GetProperty("usedMiB").GetDouble(), "disk used MiB");
        var memoryCap = Data(engine.Capacity(ds, Caller, Wire.ScopeId, null, "memory", null, null, null, 20, null));
        Equal(2, memoryCap.GetProperty("consideredMachineCount").GetInt32(), "memory machine denominator");
        Equal(1, memoryCap.GetProperty("unknownTotalCount").GetInt32(), "unknown total counted");
        Near(8192, memoryCap.GetProperty("knownTotalMiB").GetDouble(), "known memory sum");
        var bRow = memoryCap.GetProperty("page").GetProperty("rows")[1];
        Check(bRow.GetProperty("usedMiB").ValueKind == JsonValueKind.Null, "unknown used null");
        Near(512, bRow.GetProperty("freeMiB").GetDouble(), "known available memory retained despite unknown total");
        var capacityPage = Data(engine.Capacity(ds, Caller, Wire.ScopeId, null, "disk", null, null, null, 1, null));
        var capacityCursor = capacityPage.GetProperty("page").GetProperty("nextCursor").GetString();
        var capacityPage2 = Data(engine.Capacity(ds, Caller, Wire.ScopeId, null, "disk", null, null, null, 1, capacityCursor));
        Equal(V2, capacityPage2.GetProperty("page").GetProperty("rows")[0].GetProperty("entityId").GetString(), "capacity deterministic cursor");
        Fault("INVALID_CURSOR", () => engine.Capacity(ds, Caller, Wire.ScopeId, null, "memory", null, null, null, 1, capacityCursor), "capacity resource cursor bound");
        Fault("INVALID_ARGUMENT", () => engine.Capacity(ds, Caller, Wire.ScopeId, null, "disk", null, 50, 20, 20, null), "capacity bounds validate");
        var nearStale = Validate(ds.Snapshot with
        {
            Samples = ds.Snapshot.Samples.Select(m => m.Metric == "diskFreePercent"
                ? m with { ObservedAtUtc = Now.AddMinutes(-14) } : m).ToImmutableArray()
        }, source);
        var anchoredPage = Data(engine.Capacity(nearStale, Caller, Wire.ScopeId, null, "disk", null, 0, 100, 1, null));
        var anchoredCursor = anchoredPage.GetProperty("page").GetProperty("nextCursor").GetString();
        Clock.Now = Now.AddMinutes(2);
        var staleCapacity = engine.Capacity(nearStale, Caller, Wire.ScopeId, null, "disk", null, 0, 100, 1, anchoredCursor);
        Equal(2, Data(staleCapacity).GetProperty("matchingRowCount").GetInt32(), "paging counts do not drift with sample-age clock");
        Equal(V2, Data(staleCapacity).GetProperty("page").GetProperty("rows")[0].GetProperty("entityId").GetString(), "capacity second page stable across freshness boundary");
        Equal("partial", Data(staleCapacity).GetProperty("usageStatus").GetString(), "old metric timestamps flagged despite fresh snapshot");
        Clock.Now = Now;
        AssertEnvelope(engine.SourceStatus(ds));
        AssertEnvelope(engine.Error(new("SOURCE_UNAVAILABLE", "TEST_ONLY")));
    }

    private static void TestValidation(Snapshot snapshot, SourceSettings source, QueryEngine engine)
    {
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes("{bad"), Status(snapshot), source, Now), "invalid JSON refused");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes("null"), Status(snapshot), source, Now), "null snapshot refused");
        var duplicateJson = Wire.Encode(snapshot).Replace("\"schemaVersion\":\"1.0\"", "\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes(duplicateJson), Status(snapshot), source, Now), "duplicate JSON members refused");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(new byte[source.MaxSnapshotBytes + 1], Status(snapshot), source, Now), "oversize refused");
        var malformed = Wire.Encode(snapshot).Replace("\"value\":40", "\"value\":1e999");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes(malformed), Status(snapshot), source, Now), "nonfinite sample refused");
        var missingValue = Wire.Encode(snapshot).Replace("\"value\":40,", "");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes(missingValue), Status(snapshot), source, Now),
            "missing sample value refused, never zero");
        var zeroSample = snapshot.Samples[0] with { Value = 0, ObservedAtUtc = snapshot.Samples[0].ObservedAtUtc.AddSeconds(-1) };
        Check(Validate(snapshot with { Samples = snapshot.Samples.Add(zeroSample) }, source).Snapshot.Samples.Contains(zeroSample),
            "explicit zero snapshot sample retained");
        Fault("SOURCE_UNAVAILABLE", () => Validate(snapshot with { Scope = new("other", "hidden") }, source), "wrong snapshot scope refused");
        Fault("SOURCE_UNAVAILABLE", () => Validate(snapshot with { SchemaVersion = "2.0" }, source), "unknown schema refused");
        Fault("SOURCE_UNAVAILABLE", () => Validate(snapshot with { Status = "unavailable" }, source), "unavailable snapshot refused");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Bytes(snapshot), Status(snapshot) with { DatasetId = "other" }, source, Now), "status dataset mismatch refused");
        var partial = snapshot with { Devices = [snapshot.Devices.First(d => d.EntityId == A)], Volumes = snapshot.Volumes.Where(v => v.ParentEntityId == A).ToImmutableArray(),
            Samples = snapshot.Samples.Where(s => s.EntityId != B).ToImmutableArray() };
        var partialDs = Validate(partial, source);
        Equal("partial", engine.SourceStatus(partialDs).Status, "missing expected computer surfaced partial");
        Check(!engine.SourceStatus(partialDs).Coverage!.Complete, "coverage false for missing device");
        var largerExpected = partialDs with { Snapshot = partialDs.Snapshot with { ExpectedDeviceCount = 3 } };
        Equal(3, Data(engine.SourceStatus(largerExpected)).GetProperty("expectedDeviceCount").GetInt32(),
            "query engine reports dataset expected count rather than pilot constant");
        Equal(3, engine.SourceStatus(largerExpected).Coverage!.ExpectedDeviceCount,
            "query coverage uses authoritative expected count");
        Check(!engine.SourceStatus(largerExpected).Coverage!.Complete,
            "discovered rows do not imply complete expected membership");
        Fault("SOURCE_UNAVAILABLE", () => Validate(snapshot with { Devices = snapshot.Devices.SetItem(0, snapshot.Devices[0] with { Hostname = "outside.invalid" }) }, source), "host allowlist enforced");
        var noSamples = Validate(snapshot with { Samples = [] }, source);
        Equal("partial", engine.SourceStatus(noSamples).Status, "empty metrics explicit partial capability");
        Fault("SOURCE_UNAVAILABLE", () => engine.Utilization(noSamples, "cpuPercent", null, "1h", "latest", 1, false, null, null, null), "empty metrics source unavailable");
        Equal("unavailable", Data(engine.Capacity(noSamples, Caller, Wire.ScopeId, null, "memory", null, null, null, 20, null)).GetProperty("usageStatus").GetString(), "capacity empty metrics explicitly unavailable");
        var stale = Shift(snapshot, TimeSpan.FromMinutes(-20));
        Equal("stale", engine.SourceStatus(Validate(stale, source)).Freshness!.State, "stale snapshot freshness");
        Equal("partial", engine.SourceStatus(Validate(stale, source)).Status, "stale snapshot not success");
        Fault("SOURCE_UNAVAILABLE", () => Validate(Shift(snapshot, TimeSpan.FromMinutes(-61)), source), "expired snapshot unavailable");
        Fault("SOURCE_UNAVAILABLE", () => Validate(Shift(snapshot, TimeSpan.FromMinutes(5)), source), "future snapshot refused");
        var badMemory = snapshot with { Samples = snapshot.Samples.Add(new(A, "memoryAvailableMiB", Now.AddMinutes(-1), 99999, "test", true)) };
        Fault("SOURCE_UNAVAILABLE", () => Validate(badMemory, source), "impossible memory observation refused");
        var conflicting = snapshot with { Samples = snapshot.Samples.Add(snapshot.Samples[0] with { Value = 99 }) };
        Fault("SOURCE_UNAVAILABLE", () => Validate(conflicting, source), "conflicting observation duplicates refused");
    }

    private static void TestNativeMemory(Snapshot snapshot, SourceSettings source, QueryEngine engine)
    {
        var nativeSnapshot = snapshot with
        {
            Capabilities = snapshot.Capabilities with { Metrics = snapshot.Capabilities.Metrics.Add("memoryUsedPercent") },
            Samples = snapshot.Samples.AddRange(new Sample[]
            {
                new(A, "memoryUsedPercent", Now.AddMinutes(-2), 25, "SYNTHETIC_NATIVE_MEMORY_PERCENT", true),
                new(A, "memoryUsedPercent", Now.AddMinutes(-10), 50, "SYNTHETIC_NATIVE_MEMORY_PERCENT", true),
                new(B, "memoryUsedPercent", Now.AddMinutes(-3), 10, "SYNTHETIC_NATIVE_MEMORY_PERCENT", true),
                new(A, "memoryUsedPercent", Now.AddMinutes(-70), 20, "SYNTHETIC_NATIVE_MEMORY_PERCENT", true)
            })
        };
        var native = Validate(nativeSnapshot, source);
        var all = Data(engine.Utilization(native, "memoryUsedPercent", null, "1h", "latest", 20, true, "desc", null, null));
        Equal(0, all.GetProperty("missingCount").GetInt32(), "native memory works without known machine total");
        var a = all.GetProperty("rows")[0];
        Equal(A, a.GetProperty("entityId").GetString(), "native memory sort value");
        Near(25, a.GetProperty("value").GetDouble(), "native preferred over newer conflicting available-derived percent");
        Near(20, a.GetProperty("previousValue").GetDouble(), "previous native memory window");
        Near(5, a.GetProperty("changePercentagePoints").GetDouble(), "native memory delta");
        Equal("memoryUsedPercent", a.GetProperty("sourceMetric").GetString(), "native source metric identified");
        Equal("memoryUsedPercent", a.GetProperty("previousSourceMetric").GetString(), "previous native source identified");
        Equal(Now.AddMinutes(-2), a.GetProperty("coverage").GetProperty("lastSampleUtc").GetDateTimeOffset(),
            "native TimeSampled preserved");
        var mean = Data(engine.Utilization(native, "memoryUsedPercent", A, "1h", "mean", 20, false, null, null, null)).GetProperty("rows")[0];
        Near(37.5, mean.GetProperty("value").GetDouble(), "mean only native observations, not blended fallback");
        Equal(2, mean.GetProperty("coverage").GetProperty("sampleCount").GetInt32(), "native coverage excludes fallback count");
        var max = Data(engine.Utilization(native, "memoryUsedPercent", A, "1h", "max", 20, false, null, null, null)).GetProperty("rows")[0];
        Near(50, max.GetProperty("value").GetDouble(), "native maximum ignores available-derived observation");
        var threshold = Data(engine.Utilization(native, "memoryUsedPercent", null, "1h", "latest", 1, false, "asc", 20, "lt"));
        Equal(1, threshold.GetProperty("matchedCount").GetInt32(), "native memory threshold");
        Equal(B, threshold.GetProperty("rows")[0].GetProperty("entityId").GetString(), "native memory without total selected");
        Check(threshold.GetProperty("rows")[0].GetProperty("previousValue").ValueKind == JsonValueKind.Null, "no previous request remains null");

        var capacity = Data(engine.Capacity(native, Caller, Wire.ScopeId, null, "memory", null, null, null, 20, null));
        var nativeA = capacity.GetProperty("page").GetProperty("rows")[0];
        Near(8192, nativeA.GetProperty("totalMiB").GetDouble(), "normalized memory total not reinterpreted or reconverted");
        Near(2048, nativeA.GetProperty("usedMiB").GetDouble(), "native percent times total produces used MiB");
        Near(6144, nativeA.GetProperty("freeMiB").GetDouble(), "native percent capacity wins over conflicting available MiB");
        Near(75, nativeA.GetProperty("freePercent").GetDouble(), "native free percent inverse");
        Equal("memoryUsedPercent", nativeA.GetProperty("sourceMetric").GetString(), "native capacity provenance");
        Equal(Now.AddMinutes(-2), nativeA.GetProperty("observedAtUtc").GetDateTimeOffset(), "native capacity sample timestamp");
        var nativeB = capacity.GetProperty("page").GetProperty("rows")[1];
        Near(90, nativeB.GetProperty("freePercent").GetDouble(), "unknown total still permits native free percent");
        Check(nativeB.GetProperty("usedMiB").ValueKind == JsonValueKind.Null &&
            nativeB.GetProperty("freeMiB").ValueKind == JsonValueKind.Null &&
            nativeB.GetProperty("totalMiB").ValueKind == JsonValueKind.Null,
            "unknown native absolute capacity remains null, not mixed with another timestamp's available MiB");
        Equal(1, capacity.GetProperty("comparableCapacity").GetProperty("rowCount").GetInt32(), "only known native absolute capacity aggregated");
        Near(2048, capacity.GetProperty("comparableCapacity").GetProperty("usedMiB").GetDouble(), "native comparable used sum");
        var filtered = Data(engine.Capacity(native, Caller, Wire.ScopeId, null, "memory", null, 80, null, 20, null));
        Equal(1, filtered.GetProperty("matchingRowCount").GetInt32(), "native percentage supports filtering with unknown absolute total");
        Equal(0, filtered.GetProperty("comparableCapacity").GetProperty("rowCount").GetInt32(), "filtered unknown capacity never counted as known zero");

        var nativeOnlySnapshot = nativeSnapshot with
        {
            Samples = nativeSnapshot.Samples.Where(m => m.Metric != "memoryAvailableMiB").ToImmutableArray(),
            Capabilities = nativeSnapshot.Capabilities with
            {
                Metrics = nativeSnapshot.Capabilities.Metrics.Where(m => m != "memoryAvailableMiB").ToImmutableArray()
            }
        };
        var nativeOnly = Validate(nativeOnlySnapshot, source);
        Near(25, Data(engine.Utilization(nativeOnly, "memoryUsedPercent", A, "1h", "latest", 1, false, null, null, null))
            .GetProperty("rows")[0].GetProperty("value").GetDouble(), "native-only collector supported without available-MiB capability");
        Near(6144, Data(engine.Capacity(nativeOnly, Caller, Wire.ScopeId, A, "memory", null, null, null, 20, null))
            .GetProperty("page").GetProperty("rows")[0].GetProperty("freeMiB").GetDouble(), "native-only capacity supported");

        var priorOnly = Validate(nativeSnapshot with
        {
            Samples = nativeSnapshot.Samples.Where(m => m.Metric != "memoryUsedPercent" || m.ObservedAtUtc < Now.AddHours(-1)).ToImmutableArray()
        }, source);
        var fallbackEnvelope = engine.Utilization(priorOnly, "memoryUsedPercent", A, "1h", "latest", 1, true, null, null, null);
        var fallback = Data(fallbackEnvelope).GetProperty("rows")[0];
        Near(75, fallback.GetProperty("value").GetDouble(), "available-MiB fallback selected only when current window has no native");
        Equal("memoryAvailableMiB", fallback.GetProperty("sourceMetric").GetString(), "fallback source preserved after derivation");
        Equal("memoryUsedPercent", fallback.GetProperty("previousSourceMetric").GetString(), "previous source selection independently native");
        Check(fallbackEnvelope.Limitations.Contains("CURRENT_AND_PREVIOUS_MEMORY_WINDOWS_USE_DIFFERENT_SOURCE_METRICS"),
            "mixed current/previous source selection disclosed");
        var fallbackCapacity = Data(engine.Capacity(priorOnly, Caller, Wire.ScopeId, A, "memory", null, null, null, 20, null))
            .GetProperty("page").GetProperty("rows")[0];
        Near(2048, fallbackCapacity.GetProperty("freeMiB").GetDouble(), "capacity falls back when no native in freshness interval");
        Equal("memoryAvailableMiB", fallbackCapacity.GetProperty("sourceMetric").GetString(), "fallback capacity source metric");

        var oldNative = Validate(nativeSnapshot with
        {
            Samples = nativeSnapshot.Samples.Where(m => m.Metric != "memoryUsedPercent").ToImmutableArray()
                .Add(new(A, "memoryUsedPercent", Now.AddMinutes(-20), 5, "SYNTHETIC_OLD_NATIVE", true))
        }, source);
        Near(5, Data(engine.Utilization(oldNative, "memoryUsedPercent", A, "1h", "latest", 1, false, null, null, null))
            .GetProperty("rows")[0].GetProperty("value").GetDouble(), "native selected within longer utilization window");
        Near(2048, Data(engine.Capacity(oldNative, Caller, Wire.ScopeId, A, "memory", null, null, null, 20, null))
            .GetProperty("page").GetProperty("rows")[0].GetProperty("freeMiB").GetDouble(), "capacity freshness cutoff independently excludes old native");
        foreach (var invalid in new[] { -0.1, 100.1 })
            Fault("SOURCE_UNAVAILABLE", () => Validate(nativeSnapshot with
            {
                Samples = nativeSnapshot.Samples.Add(new(A, "memoryUsedPercent", Now, invalid, "SYNTHETIC_INVALID", true))
            }, source), "native percent bounded 0..100");
        var nonfinite = Wire.Encode(nativeSnapshot).Replace("\"value\":25", "\"value\":1e999");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes(nonfinite), Status(nativeSnapshot), source, Now),
            "nonfinite native percent refused");
        Fault("SOURCE_UNAVAILABLE", () => Validate(nativeSnapshot with { Capabilities = snapshot.Capabilities }, source),
            "native metric must be declared in collector capabilities");
        Fault("SOURCE_UNAVAILABLE", () => Validate(nativeSnapshot with
        {
            Samples = nativeSnapshot.Samples.Add(new(V1, "memoryUsedPercent", Now, 25, "SYNTHETIC_WRONG_ENTITY", true))
        }, source), "native memory percent cannot attach to a volume");
        foreach (var edge in new[] { 0d, 100d })
        {
            var endpoint = Validate(nativeSnapshot with
            {
                Samples = nativeSnapshot.Samples.Add(new(A, "memoryUsedPercent", Now, edge, "SYNTHETIC_BOUNDARY", true))
            }, source);
            var row = Data(engine.Capacity(endpoint, Caller, Wire.ScopeId, A, "memory", null, null, null, 20, null))
                .GetProperty("page").GetProperty("rows")[0];
            Near(8192 * edge / 100, row.GetProperty("usedMiB").GetDouble(), "native endpoint percent produces genuine zero/full used");
            Near(8192 * (100 - edge) / 100, row.GetProperty("freeMiB").GetDouble(), "native endpoint percent produces genuine zero/full free");
        }
        AssertEnvelope(engine.SourceStatus(native));
    }

    private static void TestOptimizationMetadata(Snapshot snapshot, SourceSettings source, QueryEngine engine)
    {
        var nativeSnapshot = snapshot with
        {
            Capabilities = snapshot.Capabilities with { Metrics = snapshot.Capabilities.Metrics.Add("memoryUsedPercent") },
            Samples = snapshot.Samples.AddRange(new Sample[]
            {
                new(A, "memoryUsedPercent", Now.AddMinutes(-2), 25, "SYNTHETIC_NONOPTIMIZED_MEMORY", false),
                new(A, "memoryUsedPercent", Now.AddMinutes(-5), 50, "SYNTHETIC_NONOPTIMIZED_MEMORY", false),
                new(A, "memoryUsedPercent", Now.AddMinutes(-70), 20, "SYNTHETIC_NONOPTIMIZED_MEMORY", false),
                new(B, "memoryUsedPercent", Now.AddMinutes(-3), 10, "SYNTHETIC_NONOPTIMIZED_MEMORY", false)
            })
        };
        var ds = Validate(nativeSnapshot, source);
        Equal(4, ds.Snapshot.Samples.Count(m => !m.Optimized), "native optimized=false metadata retained on ingestion");
        Check(ds.Snapshot.Samples.Any(m => m.Optimized), "optimized=true still accepted alongside false");
        var status = Data(engine.SourceStatus(ds));
        Equal(4, status.GetProperty("nonOptimizedObservationCount").GetInt32(), "status nonoptimized count retained");
        Equal(ds.Snapshot.Samples.Count(m => m.Optimized), status.GetProperty("optimizedObservationCount").GetInt32(),
            "status optimized count retained");
        var utilization = engine.Utilization(ds, "memoryUsedPercent", A, "1h", "mean", 1, true, null, null, null);
        var row = Data(utilization).GetProperty("rows")[0];
        Near(37.5, row.GetProperty("value").GetDouble(), "nonoptimized native arithmetic mean");
        Near(20, row.GetProperty("previousValue").GetDouble(), "nonoptimized native previous window");
        Equal(0, row.GetProperty("coverage").GetProperty("optimizedSampleCount").GetInt32(), "current native optimized count zero");
        Equal(2, row.GetProperty("coverage").GetProperty("nonOptimizedSampleCount").GetInt32(), "current native nonoptimized count two");
        Equal(1, row.GetProperty("previousCoverage").GetProperty("nonOptimizedSampleCount").GetInt32(), "previous native flag retained");
        Equal(false, row.GetProperty("coverage").GetProperty("fullWindowCovered").GetBoolean(), "nonoptimized does not imply full time coverage");
        Equal(false, row.GetProperty("previousCoverage").GetProperty("fullWindowCovered").GetBoolean(), "previous nonoptimized coverage conservative");
        Check(utilization.Limitations.Contains("RETAINED_OBSERVATIONS_DO_NOT_ESTABLISH_CONTINUOUS_FULL_WINDOW_COVERAGE"),
            "conservative coverage limitation includes nonoptimized series");
        var capacity = Data(engine.Capacity(ds, Caller, Wire.ScopeId, A, "memory", null, null, null, 20, null))
            .GetProperty("page").GetProperty("rows")[0];
        Near(6144, capacity.GetProperty("freeMiB").GetDouble(), "nonoptimized native capacity derived");
        Equal(false, capacity.GetProperty("optimized").GetBoolean(), "capacity selected false flag preserved");
        Equal("memoryUsedPercent", capacity.GetProperty("sourceMetric").GetString(), "nonoptimized native source wins over optimized fallback");
        var disk = Data(engine.Capacity(ds, Caller, Wire.ScopeId, V1, "disk", null, null, null, 20, null))
            .GetProperty("page").GetProperty("rows")[0];
        Equal(true, disk.GetProperty("optimized").GetBoolean(), "capacity optimized disk flag preserved");

        var mixed = Validate(nativeSnapshot with
        {
            Samples = nativeSnapshot.Samples.Add(new(A, "memoryUsedPercent", Now.AddMinutes(-8), 75, "SYNTHETIC_OPTIMIZED_MEMORY", true))
        }, source);
        var mixedRow = Data(engine.Utilization(mixed, "memoryUsedPercent", A, "1h", "mean", 1, false, null, null, null))
            .GetProperty("rows")[0];
        Near(50, mixedRow.GetProperty("value").GetDouble(), "both native flags contribute actual observations");
        Equal(1, mixedRow.GetProperty("coverage").GetProperty("optimizedSampleCount").GetInt32(), "mixed-window true flag count");
        Equal(2, mixedRow.GetProperty("coverage").GetProperty("nonOptimizedSampleCount").GetInt32(), "mixed-window false flag count");
        Equal(false, mixedRow.GetProperty("coverage").GetProperty("fullWindowCovered").GetBoolean(), "mixed-window coverage conservative");

        var allFalse = Validate(snapshot with { Samples = snapshot.Samples.Select(m => m with { Optimized = false }).ToImmutableArray() }, source);
        var cpu = Data(engine.Utilization(allFalse, "cpuPercent", A, "1h", "mean", 1, false, null, null, null)).GetProperty("rows")[0];
        Near(30, cpu.GetProperty("value").GetDouble(), "CPU false flag accepted without changing arithmetic");
        Equal(2, cpu.GetProperty("coverage").GetProperty("nonOptimizedSampleCount").GetInt32(), "CPU false metadata retained");
        var fallback = Data(engine.Utilization(allFalse, "memoryUsedPercent", A, "1h", "latest", 1, false, null, null, null)).GetProperty("rows")[0];
        Near(75, fallback.GetProperty("value").GetDouble(), "false available-MiB fallback still works");
        Equal(2, fallback.GetProperty("coverage").GetProperty("nonOptimizedSampleCount").GetInt32(), "derived fallback retains source flags");

        var nativeSample = nativeSnapshot.Samples.First(m => m.Metric == "memoryUsedPercent");
        var deduplicated = Validate(nativeSnapshot with { Samples = nativeSnapshot.Samples.Add(nativeSample) }, source);
        Equal(4, deduplicated.Snapshot.Samples.Count(m => !m.Optimized), "duplicate false observation not double-counted");
        Fault("SOURCE_UNAVAILABLE", () => Validate(nativeSnapshot with
        {
            Samples = nativeSnapshot.Samples.Add(nativeSample with { Optimized = true })
        }, source), "conflicting duplicate optimization metadata refused");
        foreach (var bad in new[] { "\"optimized\":null", "\"optimized\":\"false\"", "\"optimized\":0" })
        {
            var json = Wire.Encode(nativeSnapshot).Replace("\"optimized\":false", bad);
            Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes(json), Status(nativeSnapshot), source, Now),
                "optimized field must be an actual boolean");
        }
        var missing = Wire.Encode(nativeSnapshot).Replace(",\"optimized\":false", "");
        Fault("SOURCE_UNAVAILABLE", () => SnapshotReader.Validate(Encoding.UTF8.GetBytes(missing), Status(nativeSnapshot), source, Now),
            "missing optimized metadata is not silently treated as false");
        var rollout = Source(source.Directory);
        rollout.MaxSnapshotBytes = 10000000;
        rollout.Validate();
        Equal(ds.Snapshot.DatasetId, Validate(nativeSnapshot, rollout).Snapshot.DatasetId, "collector ten-million-byte limit supported");
        Equal(8388608, source.MaxSnapshotBytes, "default source byte limit remains unchanged");
    }

    private static async Task TestReader(string root, Snapshot snapshot, SourceSettings source)
    {
        var reader = new SnapshotReader(source, Clock);
        await FaultAsync("SOURCE_UNAVAILABLE", () => reader.ReadAsync(), "missing snapshot/status unavailable");
        await File.WriteAllBytesAsync(Path.Combine(root, "snapshot.json"), Bytes(snapshot));
        await File.WriteAllTextAsync(Path.Combine(root, "collector-status.json"), Wire.Encode(Status(snapshot)));
        Equal("synthetic-only-1", (await reader.ReadAsync()).Snapshot.DatasetId, "real file reader round trip");
        await File.WriteAllTextAsync(Path.Combine(root, "collector-status.json"), Wire.Encode(Status(snapshot) with { Status = "unavailable", ErrorCode = "SYNTHETIC_FAILURE" }));
        await FaultAsync("SOURCE_UNAVAILABLE", () => reader.ReadAsync(), "latest collection failure overrides existing snapshot");
        await File.WriteAllTextAsync(Path.Combine(root, "collector-status.json"), Wire.Encode(Status(snapshot)));
        await File.WriteAllTextAsync(Path.Combine(root, "snapshot.json"), "{");
        await FaultAsync("SOURCE_UNAVAILABLE", () => reader.ReadAsync(), "reader malformed snapshot refused");
        await File.WriteAllBytesAsync(Path.Combine(root, "snapshot.json"), new byte[source.MaxSnapshotBytes + 1]);
        await FaultAsync("SOURCE_UNAVAILABLE", () => reader.ReadAsync(), "reader oversized file refused");
    }

    private static async Task TestCapturedSnapshot(string root, Snapshot snapshot, SourceSettings source)
    {
        string[] arguments = ["--snapshot-directory", root, .. source.AllowedHostnames];
        async Task<(int Code, string Output, string Error)> Inspect(string[]? args = null, TimeProvider? clock = null)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await CapturedSnapshot.Run(args ?? arguments, clock ?? Clock, output, error);
            return (code, output.ToString(), error.ToString());
        }
        Equal(2, (await Inspect(["--unknown"])).Code, "captured mode rejects unknown arguments");
        Equal(2, (await Inspect(["--snapshot-directory", "relative", .. source.AllowedHostnames])).Code,
            "captured mode requires explicit absolute directory");
        Equal(2, (await Inspect(["--snapshot-directory", root, source.AllowedHostnames[0], source.AllowedHostnames[0]])).Code,
            "captured mode requires two distinct approved hosts");
        Equal(2, (await Inspect(["--snapshot-directory", root, "*", source.AllowedHostnames[1]])).Code,
            "captured mode refuses wildcard hosts");
        var missing = Path.Combine(root, "nonexistent-captured-input");
        Equal(1, (await Inspect(["--snapshot-directory", missing, .. source.AllowedHostnames])).Code,
            "captured mode refuses missing input");
        Check(!Directory.Exists(missing), "captured mode never creates an input directory");

        var captured = snapshot with
        {
            Status = "partial",
            Capabilities = snapshot.Capabilities with { Metrics = snapshot.Capabilities.Metrics.Add("memoryUsedPercent") },
            Samples = snapshot.Samples.Add(new(A, "memoryUsedPercent", Now.AddMinutes(-1), 25, "SYNTHETIC_NATIVE_CAPTURE", false)),
            Limitations = snapshot.Limitations.Add("SYNTHETIC_NULL_ROW_EXCLUSION_PROVENANCE")
        };
        var snapshotPath = Path.Combine(root, "snapshot.json");
        var statusPath = Path.Combine(root, "collector-status.json");
        var bytes = Bytes(captured);
        var statusBytes = Encoding.UTF8.GetBytes(Wire.Encode(Status(captured)));
        await File.WriteAllBytesAsync(snapshotPath, bytes);
        await File.WriteAllBytesAsync(statusPath, statusBytes);
        var snapshotWritten = File.GetLastWriteTimeUtc(snapshotPath);
        var statusWritten = File.GetLastWriteTimeUtc(statusPath);
        var success = await Inspect();
        Equal(0, success.Code, "captured mode accepts valid partial snapshot");
        Equal("", success.Error, "successful captured mode has no error output");
        using var report = JsonDocument.Parse(success.Output);
        var document = report.RootElement;
        Equal("captured-snapshot-read-only", document.GetProperty("mode").GetString(), "captured mode is explicitly labeled");
        Equal(Convert.ToHexString(SHA256.HashData(bytes)), document.GetProperty("snapshotSha256").GetString(),
            "captured report binds exact input bytes");
        Equal(10000000, document.GetProperty("maxSnapshotBytes").GetInt32(), "captured mode uses collector rollout byte bound");
        Equal(false, document.GetProperty("authenticationValidated").GetBoolean(), "captured mode does not claim authentication");
        Equal(false, document.GetProperty("liveScomConnectivityValidated").GetBoolean(), "captured mode does not claim live SDK access");
        var queries = document.GetProperty("queries");
        Equal(10, queries.EnumerateObject().Count(), "captured mode runs six tool types across entities, metrics and resources");
        Equal("partial", queries.GetProperty("get_source_status").GetProperty("envelope").GetProperty("status").GetString(),
            "captured mode preserves partial source status");
        Check(queries.GetProperty("get_source_status").GetProperty("envelope").GetProperty("limitations")
            .EnumerateArray().Any(l => l.GetString() == "SYNTHETIC_NULL_ROW_EXCLUSION_PROVENANCE"),
            "captured mode preserves exclusion limitations");
        var nativeGroup = document.GetProperty("observationsByRule").EnumerateArray()
            .Single(g => g.GetProperty("metric").GetString() == "memoryUsedPercent");
        Equal(false, nativeGroup.GetProperty("optimized").GetBoolean(), "captured mode retains optimized=false rule metadata");
        Equal(1, nativeGroup.GetProperty("sampleCount").GetInt32(), "captured rule count reflects real retained observations");
        Equal(Now.AddMinutes(-1), nativeGroup.GetProperty("firstObservedAtUtc").GetDateTimeOffset(),
            "captured rule timestamp is source backed");
        var nativeRow = queries.GetProperty("get_utilization:memoryUsedPercent").GetProperty("envelope")
            .GetProperty("data").GetProperty("rows")[0];
        Equal(false, nativeRow.GetProperty("coverage").GetProperty("fullWindowCovered").GetBoolean(),
            "captured utilization does not claim complete coverage");
        Check(bytes.SequenceEqual(await File.ReadAllBytesAsync(snapshotPath)), "captured mode does not rewrite snapshot bytes");
        Check(statusBytes.SequenceEqual(await File.ReadAllBytesAsync(statusPath)), "captured mode does not rewrite status bytes");
        Equal(snapshotWritten, File.GetLastWriteTimeUtc(snapshotPath), "captured mode leaves snapshot modification time unchanged");
        Equal(statusWritten, File.GetLastWriteTimeUtc(statusPath), "captured mode leaves status modification time unchanged");
        Equal(2, Directory.GetFiles(root).Length, "captured mode writes no extra files");

        Equal(1, (await Inspect(["--snapshot-directory", root, "outside.example.invalid", source.AllowedHostnames[1]])).Code,
            "captured mode refuses records outside the supplied allowlist");
        var expired = await Inspect(clock: new ManualClock(Now.AddMinutes(61)));
        Equal(1, expired.Code, "captured mode keeps real source expiry protection");
        Check(expired.Error.Contains("SOURCE_EXPIRED_OR_INVALID_TIME", StringComparison.Ordinal), "captured expiry reason is explicit");
        Equal("", expired.Output, "failed captured mode emits no success report");
        var stale = await Inspect(clock: new ManualClock(Now.AddMinutes(16)));
        Equal(0, stale.Code, "stale but not expired captured source may be inspected");
        using var staleReport = JsonDocument.Parse(stale.Output);
        Equal("stale", staleReport.RootElement.GetProperty("queries").GetProperty("get_source_status")
            .GetProperty("envelope").GetProperty("freshness").GetProperty("state").GetString(), "captured mode reports staleness honestly");

        await File.WriteAllTextAsync(statusPath, Wire.Encode(Status(captured) with { DatasetId = "SYNTHETIC_MISMATCH" }));
        Equal(1, (await Inspect()).Code, "captured mode refuses nonmatching status/snapshot pair");
        await File.WriteAllTextAsync(statusPath, Wire.Encode(Status(captured) with { Status = "unavailable", ErrorCode = "SYNTHETIC_FAILURE" }));
        Equal(1, (await Inspect()).Code, "captured mode refuses failed latest collector status");
        await File.WriteAllBytesAsync(statusPath, statusBytes);
        await File.WriteAllBytesAsync(snapshotPath, Bytes(captured with { Samples = [] }));
        var empty = await Inspect();
        Equal(0, empty.Code, "empty observations do not invalidate an otherwise valid partial snapshot");
        using var emptyReport = JsonDocument.Parse(empty.Output);
        Equal(0, emptyReport.RootElement.GetProperty("observationsByRule").GetArrayLength(), "empty capture never invents samples");
        var unavailable = emptyReport.RootElement.GetProperty("queries").GetProperty("get_utilization:cpuPercent");
        Equal(true, unavailable.GetProperty("isError").GetBoolean(), "captured mode surfaces unavailable metric result");
        Equal("unavailable", unavailable.GetProperty("envelope").GetProperty("status").GetString(), "missing metrics are not called successful utilization");
    }

    private static async Task TestHttp(AuthSettings auth, SourceSettings source, Dataset ds)
    {
        using var rsa = RSA.Create(2048);
        var signing = new RsaSecurityKey(rsa) { KeyId = "synthetic-validation-key" };
        using var wrongRsa = RSA.Create(2048);
        var wrongSigning = new RsaSecurityKey(wrongRsa) { KeyId = "synthetic-wrong-key" };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.Logging.ClearProviders();
        using var requestErrors = new RequestErrorLogger();
        builder.Logging.AddProvider(requestErrors);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        Hosting.AddScomServices(builder.Services, auth, source);
        var memory = new MemoryReader(ds);
        builder.Services.AddSingleton<ISnapshotReader>(memory);
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            // Only this synthetic harness replaces network discovery with an in-memory public signing key.
            o.Authority = null;
            o.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0"
            };
            o.Configuration.SigningKeys.Add(signing);
        });
        await using var app = builder.Build();
        Hosting.MapScom(app);
        app.MapGet("/synthetic-storage-failure", (Func<string>)(() =>
            throw new Microsoft.Data.Sqlite.SqliteException("private-sensitive-marker", 14, 14)));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            using var storageFailure = await client.GetAsync("/synthetic-storage-failure");
            Equal(HttpStatusCode.InternalServerError, storageFailure.StatusCode, "storage failure remains HTTP 500");
            Equal("{\"error\":\"REQUEST_FAILED\"}", await storageFailure.Content.ReadAsStringAsync(),
                "storage failure response remains sanitized");
            Equal(true, requestErrors.Messages.Any(message => message.Contains("ErrorType=SqliteException") &&
                message.Contains("SqliteErrorCode=14") && message.Contains("SqliteExtendedErrorCode=14")),
                "storage diagnostic records safe exception and SQLite codes");
            Equal(true, requestErrors.Messages.All(message => !message.Contains("private-sensitive-marker")),
                "storage diagnostic excludes exception message");
            var health = await client.GetStringAsync("/healthz");
            using var healthJson = JsonDocument.Parse(health);
            Equal(2, healthJson.RootElement.EnumerateObject().Count(), "public health exposes service/version only");
            var initialize = new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                protocolVersion = "2025-03-26", capabilities = new { },
                clientInfo = new { name = "synthetic-validation", version = "1" }
            } };
            var unauth = await Send(client, initialize);
            Equal(HttpStatusCode.Unauthorized, unauth.StatusCode, "anonymous MCP 401");
            using var unsupportedGet = await client.GetAsync("/mcp");
            Equal(HttpStatusCode.MethodNotAllowed, unsupportedGet.StatusCode, "stateless MCP does not expose a GET stream");
            Equal("", await unsupportedGet.Content.ReadAsStringAsync(), "unsupported MCP GET exposes no content");
            await TokenDenied(Token(auth, wrongSigning), HttpStatusCode.Unauthorized, "wrong signature 401");
            await TokenDenied(Token(auth, signing, audience: "wrong"), HttpStatusCode.Unauthorized, "wrong audience 401");
            await TokenDenied(Token(auth, signing, audience: auth.Audiences[0] + "/"), HttpStatusCode.Unauthorized, "audience trailing slash not accepted");
            await TokenDenied(Token(auth, signing, issuer: "https://issuer.invalid"), HttpStatusCode.Unauthorized, "wrong issuer 401");
            await TokenDenied(Token(auth, signing, expired: true), HttpStatusCode.Unauthorized, "expired JWT 401");
            await TokenDenied(Token(auth, signing, scope: "other"), HttpStatusCode.Forbidden, "missing delegated scope 403");
            await TokenDenied(Token(auth, signing, oid: Guid.Empty.ToString()), HttpStatusCode.Forbidden, "unmapped caller 403");
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(auth, signing));
            var init = await Send(client, initialize);
            Equal(HttpStatusCode.OK, init.StatusCode, "valid JWT initializes MCP");
            var listing = await Send(client, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            var tools = Rpc(await listing.Content.ReadAsStringAsync()).GetProperty("result").GetProperty("tools");
            Equal(8, tools.GetArrayLength(), "six existing tools plus additive lifecycle and opt-in live verification");
            var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray();
            Check(names.SequenceEqual(new[] { "get_capacity_summary", "get_device_details", "get_inventory_summary",
                "get_source_status", "get_utilization", "list_devices", "list_os_support_assessments", "verify_object_current_state" }), "exact tool names");
            foreach (var tool in tools.EnumerateArray())
            {
                var annotations = tool.GetProperty("annotations");
                Check(annotations.GetProperty("readOnlyHint").GetBoolean(), "readOnlyHint on " + tool.GetProperty("name"));
                Check(!annotations.GetProperty("destructiveHint").GetBoolean(), "non-destructive metadata");
                Equal(tool.GetProperty("name").GetString() != "verify_object_current_state",
                    annotations.GetProperty("idempotentHint").GetBoolean(), "idempotent metadata");
                foreach (var parameter in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
                    Check(!parameter.Value.ToString().Contains("\"type\": \"object\"", StringComparison.Ordinal), "flat schema parameter " + parameter.Name);
            }
            var forbidden = await Call("get_source_status", new { scopeId = "unknown" });
            Equal(HttpStatusCode.Forbidden, forbidden.StatusCode, "forbidden tool scope HTTP403");
            var valid = await Call("get_inventory_summary", new { });
            var result = Rpc(await valid.Content.ReadAsStringAsync()).GetProperty("result");
            Check(!result.GetProperty("isError").GetBoolean(), "successful tool non-error");
            var envelope = TextEnvelope(result);
            Equal(2, envelope.GetProperty("data").GetProperty("uniqueComputerCount").GetInt32(), "MCP tool returns actual filtered count");
            Equal("1.0", envelope.GetProperty("schemaVersion").GetString(), "MCP envelope preserved");
            var liveDisabled = await Call("verify_object_current_state", new { objectId = A });
            Equal("LIVE_NOT_ENABLED", TextEnvelope(Rpc(await liveDisabled.Content.ReadAsStringAsync()).GetProperty("result"))
                .GetProperty("error").GetString(), "targeted live is disabled without source fallback");
            var lifecycleDenied = await Call("list_os_support_assessments", new { scopeId = "unknown" });
            Equal(HttpStatusCode.Forbidden, lifecycleDenied.StatusCode, "lifecycle scope denied over HTTP");
            var lifecycleResponse = await Call("list_os_support_assessments", new { pageSize = 1 });
            var lifecycleData = TextEnvelope(Rpc(await lifecycleResponse.Content.ReadAsStringAsync()).GetProperty("result")).GetProperty("data");
            Equal(2, lifecycleData.GetProperty("totalCount").GetInt32(), "legacy HTTP lifecycle full retained count");
            Equal(1, lifecycleData.GetProperty("rows").GetArrayLength(), "legacy HTTP lifecycle bounded page");
            var lifecycleNext = await Call("list_os_support_assessments",
                new { pageSize = 1, cursor = lifecycleData.GetProperty("nextCursor").GetString() });
            Equal(1, TextEnvelope(Rpc(await lifecycleNext.Content.ReadAsStringAsync()).GetProperty("result"))
                .GetProperty("data").GetProperty("rows").GetArrayLength(), "legacy HTTP lifecycle continuation");
            var lifecycleInvalid = await Call("list_os_support_assessments", new { assessment = "invalid" });
            Equal(-32602, Rpc(await lifecycleInvalid.Content.ReadAsStringAsync()).GetProperty("error").GetProperty("code").GetInt32(),
                "lifecycle invalid arguments JSON-RPC");
            var missing = await Call("get_device_details", new { entityId = Guid.Empty.ToString() });
            var missingResult = Rpc(await missing.Content.ReadAsStringAsync()).GetProperty("result");
            Check(missingResult.GetProperty("isError").GetBoolean(), "missing entity isError");
            Equal("NOT_FOUND", TextEnvelope(missingResult).GetProperty("data").GetProperty("errorCode").GetString(), "missing entity opaque error");
            var invalid = await Call("list_devices", new { pageSize = 100 });
            var invalidResult = Rpc(await invalid.Content.ReadAsStringAsync());
            Equal(-32602, invalidResult.GetProperty("error").GetProperty("code").GetInt32(), "invalid arguments JSON-RPC -32602");
            memory.Current = Validate(ds.Snapshot with
            {
                Capabilities = ds.Snapshot.Capabilities with { Metrics = ds.Snapshot.Capabilities.Metrics.Add("memoryUsedPercent") },
                Samples = ds.Snapshot.Samples.Add(new(A, "memoryUsedPercent", Now.AddMinutes(-2), 25, "SYNTHETIC_NATIVE_HTTP", false))
            }, source);
            var nativeResponse = await Call("get_utilization", new { metric = "memoryUsedPercent", entityId = A });
            var nativeResult = Rpc(await nativeResponse.Content.ReadAsStringAsync()).GetProperty("result");
            Check(!nativeResult.GetProperty("isError").GetBoolean(), "native memory succeeds over authenticated MCP HTTP");
            var nativeRow = TextEnvelope(nativeResult).GetProperty("data").GetProperty("rows")[0];
            Near(25, nativeRow.GetProperty("value").GetDouble(), "MCP response uses native not fallback percentage");
            Equal("memoryUsedPercent", nativeRow.GetProperty("sourceMetric").GetString(), "MCP native provenance preserved");
            Equal(1, nativeRow.GetProperty("coverage").GetProperty("nonOptimizedSampleCount").GetInt32(), "MCP preserves native optimized=false count");
            Equal(false, nativeRow.GetProperty("coverage").GetProperty("fullWindowCovered").GetBoolean(), "MCP nonoptimized coverage not overstated");
            var nativeCapacity = await Call("get_capacity_summary", new { resource = "memory", entityId = A });
            var nativeCapacityRow = TextEnvelope(Rpc(await nativeCapacity.Content.ReadAsStringAsync()).GetProperty("result"))
                .GetProperty("data").GetProperty("page").GetProperty("rows")[0];
            Near(6144, nativeCapacityRow.GetProperty("freeMiB").GetDouble(), "MCP native capacity math");
            Equal(Now.AddMinutes(-2), nativeCapacityRow.GetProperty("observedAtUtc").GetDateTimeOffset(), "MCP native capacity timestamp");
            Equal(false, nativeCapacityRow.GetProperty("optimized").GetBoolean(), "MCP native capacity optimized=false preserved");
            memory.Fail = true;
            var lifecycleInvalidWhileUnavailable = await Call("list_os_support_assessments", new { pageSize = 0 });
            Equal(-32602, Rpc(await lifecycleInvalidWhileUnavailable.Content.ReadAsStringAsync()).GetProperty("error").GetProperty("code").GetInt32(),
                "lifecycle argument validation precedes unavailable source read");
            var lifecycleFailed = await Call("list_os_support_assessments", new { });
            Equal("SOURCE_UNAVAILABLE", TextEnvelope(Rpc(await lifecycleFailed.Content.ReadAsStringAsync()).GetProperty("result"))
                .GetProperty("data").GetProperty("errorCode").GetString(), "lifecycle never bypasses unavailable inventory");
            var invalidWhileUnavailable = await Call("get_utilization", new { metric = "invalid" });
            Equal(-32602, Rpc(await invalidWhileUnavailable.Content.ReadAsStringAsync()).GetProperty("error").GetProperty("code").GetInt32(),
                "argument validation precedes source availability");
            var sourceFailed = await Call("get_source_status", new { });
            var errorResult = Rpc(await sourceFailed.Content.ReadAsStringAsync()).GetProperty("result");
            Check(errorResult.GetProperty("isError").GetBoolean(), "source failure isError");
            Equal("SOURCE_UNAVAILABLE", TextEnvelope(errorResult).GetProperty("data").GetProperty("errorCode").GetString(), "source failure explicit safe error");

            async Task TokenDenied(string token, HttpStatusCode expected, string name)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await Send(client, initialize);
                Equal(expected, response.StatusCode, name);
            }
            Task<HttpResponseMessage> Call(string name, object arguments) =>
                Send(client, new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name, arguments } });
        }
        finally { await app.StopAsync(); }
    }

    private sealed class RequestErrorLogger : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) =>
            categoryName == "ScomRequest" ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Wire.Encode(payload), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
        return client.SendAsync(request);
    }
    private static JsonElement Rpc(string text)
    {
        if (text.StartsWith("event:", StringComparison.Ordinal) || text.StartsWith("data:", StringComparison.Ordinal))
            text = string.Join("", text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
    private static JsonElement TextEnvelope(JsonElement result) => Rpc(result.GetProperty("content")[0].GetProperty("text").GetString()!);
    private static string Token(AuthSettings auth, SecurityKey key, string? audience = null, string? issuer = null,
        string? scope = null, string? oid = null, bool expired = false)
    {
        var now = DateTime.UtcNow;
        var claims = new[] { new Claim("tid", auth.TenantId), new Claim("oid", oid ?? Oid), new Claim("scp", scope ?? auth.RequiredScope) };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer ?? $"https://login.microsoftonline.com/{auth.TenantId}/v2.0", audience ?? auth.Audiences[0],
            claims, now.AddHours(-2), expired ? now.AddHours(-1) : now.AddMinutes(10),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
    }

    private static Snapshot Fixture()
    {
        ImmutableArray<string> refs = ["SCOM:00000000-0000-0000-0000-000000000088"];
        var a = new Device(A, A, "synthetic-a.example.invalid", "computer", "windows", "SYNTHETIC OS", "healthy",
            "Success", "virtual", "agent", 8192, 4, null, [Wire.ScopeId], refs);
        var b = new Device(B, B, "synthetic-b.example.invalid", "computer", "windows", null, "critical",
            "Error", "unknown", "agent", null, null, null, [Wire.ScopeId], refs);
        var samples = ImmutableArray.Create(
            new Sample(A, "cpuPercent", Now.AddMinutes(-1), 40, "SYNTHETIC_CPU", true),
            new Sample(A, "cpuPercent", Now.AddMinutes(-10), 20, "SYNTHETIC_CPU", true),
            new Sample(B, "cpuPercent", Now.AddMinutes(-1), 90, "SYNTHETIC_CPU", true),
            new Sample(A, "cpuPercent", Now.AddMinutes(-80), 10, "SYNTHETIC_CPU", true),
            new Sample(B, "cpuPercent", Now.AddMinutes(-70), 80, "SYNTHETIC_CPU", true),
            new Sample(A, "memoryAvailableMiB", Now.AddMinutes(-1), 2048, "SYNTHETIC_MEMORY", true),
            new Sample(A, "memoryAvailableMiB", Now.AddMinutes(-5), 4096, "SYNTHETIC_MEMORY", true),
            new Sample(B, "memoryAvailableMiB", Now.AddMinutes(-1), 512, "SYNTHETIC_MEMORY", true),
            new Sample(V1, "diskFreePercent", Now.AddMinutes(-1), 10, "SYNTHETIC_DISK", true),
            new Sample(V2, "diskFreePercent", Now.AddMinutes(-1), 75, "SYNTHETIC_DISK", true));
        return new("1.0", "synthetic-only-1", "scom", "ok",
            new("00000000-0000-0000-0000-000000000088", "SYNTHETIC-TEST-ONLY"), new(Wire.ScopeId, "SYNTHETIC TEST FIXTURE"),
            Now, Now.AddMinutes(-2), 2, [b, a with { MemoryTotalMiB = null }, a],
            [new(V1, A, "C:", "NTFS", 10000, refs), new(V2, A, "D:", "NTFS", 2000, refs), new(V3, B, "C:", null, null, refs)],
            samples.Add(samples[0]), new(["WindowsComputer", "OperatingSystem", "LogicalDisk"],
                ["cpuPercent", "memoryAvailableMiB", "diskFreePercent"], false, false),
            ["SYNTHETIC_FIXTURE_ONLY_NOT_REAL_SCOM_DATA"], refs);
    }
    private static Snapshot Shift(Snapshot s, TimeSpan by) => s with
    {
        CollectedAtUtc = s.CollectedAtUtc + by, CollectionStartedAtUtc = s.CollectionStartedAtUtc + by,
        Samples = s.Samples.Select(m => m with { ObservedAtUtc = m.ObservedAtUtc + by }).ToImmutableArray()
    };
    private static SourceSettings Source(string root) => new()
    {
        Directory = root, AllowedHostnames = ["synthetic-a.example.invalid", "synthetic-b.example.invalid"]
    };
    private static AuthSettings Auth() => new()
    {
        TenantId = SyntheticTenant, Audiences = ["api://synthetic-scom-validation"],
        RequiredScope = "Scom.Read.Synthetic",
        Callers = [new() { ObjectId = Oid, Scopes = [Wire.ScopeId] }]
    };
    private static ClaimsPrincipal Principal(string? scope = null, string? oid = null, string? tid = null, Claim[]? extras = null) =>
        new(new ClaimsIdentity(new[] { new Claim("tid", tid ?? SyntheticTenant),
            new Claim("scp", scope ?? "Scom.Read.Synthetic"), new Claim("oid", oid ?? Oid) }.Concat(extras ?? []), "synthetic-test"));
    private static CollectorStatus Status(Snapshot s) => new("ok", s.CollectedAtUtc, null, s.DatasetId);
    private static byte[] Bytes(Snapshot s) => Encoding.UTF8.GetBytes(Wire.Encode(s));
    private static Dataset Validate(Snapshot s, SourceSettings settings) => SnapshotReader.Validate(Bytes(s), Status(s), settings, Now);
    private static JsonElement Data(Envelope e) => JsonSerializer.SerializeToElement(e.Data, Wire.Json);
    private static void AssertEnvelope(Envelope e)
    {
        var json = JsonSerializer.SerializeToElement(e, Wire.Json);
        foreach (var name in new[] { "schemaVersion", "status", "sourceMode", "datasetId", "scope", "data",
                     "asOfUtc", "freshness", "coverage", "limitations", "sourceRefs", "correlationId" })
            Check(json.TryGetProperty(name, out _), "envelope " + name);
    }
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "validate.ps1"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run harness from its implementation directory.");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++;
    }
    private static void Equal<T>(T expected, T actual, string name) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{name}; expected={expected}, actual={actual}");
    private static void Near(double expected, double actual, string name) => Check(Math.Abs(expected - actual) < 0.000001, name);
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { passed++; return; }
        throw new InvalidOperationException("FAIL: " + name);
    }
    private static void Fault(string code, Action action, string name)
    {
        try { action(); }
        catch (QueryFault error) when (error.Code == code) { passed++; return; }
        throw new InvalidOperationException("FAIL: " + name);
    }
    private static async Task FaultAsync(string code, Func<Task<Dataset>> action, string name)
    {
        try { await action(); }
        catch (QueryFault error) when (error.Code == code) { passed++; return; }
        throw new InvalidOperationException("FAIL: " + name);
    }
}

sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class MemoryReader(Dataset dataset) : ISnapshotReader
{
    public Dataset Current { get; set; } = dataset;
    public bool Fail { get; set; }
    public Task<Dataset> ReadAsync(CancellationToken ct = default) => Fail
        ? throw new QueryFault("SOURCE_UNAVAILABLE", "SYNTHETIC_TEST_FAILURE")
        : Task.FromResult(Current);
}
