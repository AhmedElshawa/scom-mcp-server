using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ScomMcp;

static class OsLifecycleChecks
{
    public static void Run()
    {
        var assertions = 0;
        var clock = new Clock { Now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero) };
        var source = new SourceSettings { SnapshotEnabled = false };
        var caller = new Caller("synthetic-lifecycle", ImmutableHashSet.Create(Wire.ScopeId));
        var codec = new CursorCodec(clock, source);
        var engine = new QueryEngine(source, clock, codec);
        var device = new Device(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            "synthetic.invalid", "computer", "windows", "Microsoft Windows Server 2016 Standard",
            "unknown", "unknown", "unknown", "agent", null, null, null, [], []);
        var snapshot = new Snapshot("1.0", "synthetic-lifecycle", "scom", "ok",
            new(Guid.NewGuid().ToString("D"), "Synthetic"), new(Wire.ScopeId, "Synthetic"),
            clock.Now, clock.Now, 6, [], [], [], new([], [], false, false), [], []);
        var devices = new[]
        {
            device,
            device with { EntityId = Guid.NewGuid().ToString("D"), Hostname = "b.invalid", OsVersion = "Microsoft Windows Server 2022 Datacenter" },
            device with { EntityId = Guid.NewGuid().ToString("D"), Hostname = "c.invalid", OsVersion = "Windows Server 2012 R2 Standard" },
            device with { EntityId = Guid.NewGuid().ToString("D"), Hostname = "d.invalid", OsVersion = "Windows Server 2025" },
            device with { EntityId = Guid.NewGuid().ToString("D"), Hostname = "e.invalid", OsVersion = null },
            device with { EntityId = Guid.NewGuid().ToString("D"), Hostname = "f.invalid", OsFamily = "linux", OsVersion = "Synthetic Linux" }
        }.ToImmutableArray();
        var ds = new Dataset(snapshot with { Devices = devices }, "synthetic-fingerprint", 0, 0, 0, []);
        var today = new DateOnly(2026, 11, 2);

        Equal(365, new LifecycleSettings().ApproachingSupportDays, "documented threshold default");
        Equal(90, new LifecycleSettings().MaxCatalogAgeDays, "documented evidence freshness default");
        foreach (var invalid in new[] { 0, -1, 3651 })
            Invalid(() => new LifecycleSettings { ApproachingSupportDays = invalid }.Validate(), "threshold rejected");
        foreach (var invalid in new[] { 0, -1, 91 })
            Invalid(() => new LifecycleSettings { MaxCatalogAgeDays = invalid }.Validate(), "freshness cannot be extended beyond 90 days");
        using (var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"Lifecycle":{"ApproachingSupportDays":30,"MaxCatalogAgeDays":45}}""")))
        {
            var settings = new ConfigurationBuilder().AddJsonStream(json).Build().GetSection("Lifecycle").Get<LifecycleSettings>()!;
            Equal(30, settings.ApproachingSupportDays, "real JSON provider threshold binding");
            Equal(45, settings.MaxCatalogAgeDays, "real JSON provider freshness binding");
        }
        foreach (var product in OsLifecycleCatalog.Products)
        {
            foreach (var edition in product.Editions)
            {
                var matching = device with { OsVersion = $"Microsoft {product.Product} {edition}" };
                Equal(product.Product, Assess(matching, today).Product, "every allowed exact product/edition alias");
                Equal(edition, Assess(matching, today).Edition, "observed edition preserved");
            }
            var d = device with { OsVersion = $"{product.Product} Standard" };
            Equal("mainstream", Assess(d, product.MainstreamEnd).Assessment, "mainstream end date inclusive");
            Equal("extended", Assess(d, product.MainstreamEnd.AddDays(1)).Assessment, "day after mainstream is extended not unsupported");
            Equal("extended", Assess(d, product.ExtendedEnd).Assessment, "extended end date inclusive");
            Equal(0, Assess(d, product.ExtendedEnd).DaysToExtendedEnd, "zero days remaining on last day");
            Equal(true, Assess(d, product.ExtendedEnd).ApproachingExtendedEnd, "last supported day approaching");
            Equal("ended", Assess(d, product.ExtendedEnd.AddDays(1)).Assessment, "day after extended end unsupported baseline");
            Equal(-1, Assess(d, product.ExtendedEnd.AddDays(1)).DaysToExtendedEnd, "negative days after support");
            Equal(false, Assess(d, product.ExtendedEnd.AddDays(1)).ApproachingExtendedEnd, "ended not approaching");
            Equal(true, Assess(d, product.ExtendedEnd.AddDays(-365)).ApproachingExtendedEnd, "inclusive approaching threshold");
            Equal(false, Assess(d, product.ExtendedEnd.AddDays(-366)).ApproachingExtendedEnd, "outside threshold");
            Equal("unknown", Assess(d, product.Start.AddDays(-1)).Assessment, "no conclusions before product start");
            Equal("unknown", OsLifecycleCatalog.Assess(d, today, false, 365).Assessment, "stale evidence never classifies");
            Equal(null, OsLifecycleCatalog.Assess(d, today, false, 365).DaysToExtendedEnd, "stale evidence has no countdown");
        }
        foreach (var caption in new[] { "Windows Server 2025", "10.0.26100", "Windows Server 2022 Standard build 20348",
            "Windows Server 2022 Standard Evaluation", "Windows Server 2022 Datacenter: Azure Edition",
            "Windows Server 2022 Enterprise", "Windows Server, version 23H2 Standard", "Windows Server 2012 Standard",
            "Microsoft Windows Server 2016 MultiPoint Premium", "Windows 11 Pro", "", "  " })
            Equal("unknown", Assess(device with { OsVersion = caption }, today).Assessment, "ambiguous/outside-profile caption remains unknown");
        Equal("unknown", Assess(device with { OsVersion = null }, today).Assessment, "missing OS not assumed");
        var oldDeviceJson = Wire.Encode(device).Replace(",\"osCaption\":null", "", StringComparison.Ordinal);
        Equal(null, JsonSerializer.Deserialize<Device>(oldDeviceJson, Wire.Json)!.OsCaption,
            "pre-caption device payload remains readable");
        foreach (var build in new[] { "10.0.20348", "10.0.26100" })
        {
            var observed = device with { OsVersion = build, OsCaption = "Microsoft Windows Server 2022 Datacenter" };
            Equal("Windows Server 2022", Assess(observed, today).Product, "explicit caption takes precedence, never inferred build");
            Equal(observed.OsCaption, Assess(observed, today).ObservedOsCaption, "lifecycle returns actual caption evidence");
            Equal("unknown", Assess(observed with { OsCaption = null }, today).Assessment, "build alone remains unknown");
            Equal("unknown", Assess(observed with { OsCaption = "Windows Server 2022 Enterprise" }, today).Assessment,
                "unsupported caption does not fall back to version");
        }
        Equal("unknown", Assess(device with { OsCaption = "Windows Server 2022 Enterprise" }, today).Assessment,
            "unmapped caption takes precedence even over a mapped legacy version");
        Equal("unsupported", Assess(device with { OsFamily = "unknown" }, today).Assessment, "unknown family outside profile");
        Equal("unsupported", Assess(devices[5], today).Assessment, "Linux inventory preserved but not assessed");
        Equal("extended", Assess(device with { OsVersion = " microsoft windows server 2022 standard " }, today).Assessment,
            "only whitespace/case normalization permitted");
        var esu = Assess(devices[2], new(2025, 11, 2));
        Equal("ended", esu.Assessment, "ESU availability does not extend baseline support");
        Equal(new DateOnly(2026, 10, 13), esu.OptionalEsuEnd, "optional ESU catalog deadline");
        Equal("notAssessed", esu.EsuEntitlement, "no inferred ESU entitlement");
        Equal(true, esu.EvidenceUrls.Contains(OsLifecycleCatalog.EsuSource), "ESU source cited");

        var first = Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 2, null));
        Equal(6, first.GetProperty("scopeComputerCount").GetInt32(), "all computers counted before pagination");
        Equal(3, first.GetProperty("classifiedComputerCount").GetInt32(), "full classified count");
        Equal(2, first.GetProperty("unknownComputerCount").GetInt32(), "unknown coverage reported");
        Equal(1, first.GetProperty("unsupportedComputerCount").GetInt32(), "unsupported coverage reported");
        Equal(false, first.GetProperty("assessmentComplete").GetBoolean(), "unknowns prevent complete classification");
        Equal(6, first.GetProperty("totalCount").GetInt32(), "total is not page length");
        Equal(1, first.GetProperty("approachingExtendedEndCount").GetInt32(), "count across all retained computers");
        Equal("fresh", first.GetProperty("catalogState").GetString(), "verified evidence status");
        Equal(true, first.GetProperty("rows")[0].GetProperty("evidenceUrls").GetArrayLength() > 0, "source citations present");
        var seen = new HashSet<string>();
        var page = first;
        while (true)
        {
            foreach (var row in page.GetProperty("rows").EnumerateArray())
                Equal(true, seen.Add(row.GetProperty("entityId").GetString()!), "pagination no duplicates");
            var next = page.GetProperty("nextCursor").GetString();
            if (next is null) break;
            page = Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 2, next));
        }
        Equal(6, seen.Count, "pagination covers every retained computer");
        var cursor = first.GetProperty("nextCursor").GetString();
        Fault(() => engine.OsSupport(ds, caller with { Binding = "different" }, Wire.ScopeId, null, 2, cursor), "INVALID_CURSOR", "caller bound");
        Fault(() => engine.OsSupport(ds, caller, "different", null, 2, cursor), "INVALID_CURSOR", "scope bound");
        Fault(() => engine.OsSupport(ds, caller, Wire.ScopeId, "ended", 2, cursor), "INVALID_CURSOR", "filter bound");
        Fault(() => engine.OsSupport(ds, caller, Wire.ScopeId, null, 1, cursor), "INVALID_CURSOR", "page size bound");
        Fault(() => engine.OsSupport(ds with { Fingerprint = "replacement" }, caller, Wire.ScopeId, null, 2, cursor),
            "CURSOR_DATASET_CHANGED", "dataset bound");
        var differentPolicy = new QueryEngine(source, clock, codec, new() { ApproachingSupportDays = 30 });
        Fault(() => differentPolicy.OsSupport(ds, caller, Wire.ScopeId, null, 2, cursor), "INVALID_CURSOR", "policy bound");
        Fault(() => engine.OsSupport(ds, caller, Wire.ScopeId, "nonsense", 2, null), "INVALID_ARGUMENT", "filter validated");
        Fault(() => engine.OsSupport(ds, caller, Wire.ScopeId, null, 51, null), "INVALID_ARGUMENT", "page budget validated");
        var approaching = Data(engine.OsSupport(ds, caller, Wire.ScopeId, "approaching", 1, null));
        Equal(1, approaching.GetProperty("totalCount").GetInt32(), "approaching exact filter");
        Equal(6, approaching.GetProperty("scopeComputerCount").GetInt32(), "filter does not hide coverage denominator");
        var missing = Data(engine.OsSupport(ds with { Snapshot = ds.Snapshot with { ExpectedDeviceCount = 8 } },
            caller, Wire.ScopeId, null, 50, null));
        Equal(2, missing.GetProperty("missingComputerCount").GetInt32(), "expected missing devices not assessed as present");
        Equal(false, missing.GetProperty("assessmentComplete").GetBoolean(), "missing inventory not complete");
        var known = ds with { Snapshot = ds.Snapshot with { Devices = devices.Take(3).ToImmutableArray(), ExpectedDeviceCount = 3 } };
        Equal(true, Data(engine.OsSupport(known, caller, Wire.ScopeId, null, 50, null))
            .GetProperty("assessmentComplete").GetBoolean(), "known fresh complete inventory classification");
        Equal(false, Data(engine.OsSupport(known with { Snapshot = known.Snapshot with { Status = "partial" } },
            caller, Wire.ScopeId, null, 50, null)).GetProperty("assessmentComplete").GetBoolean(), "partial inventory never complete");
        Equal(false, Data(engine.OsSupport(known with { Snapshot = known.Snapshot with { CollectedAtUtc = clock.Now.AddMinutes(-16) } },
            caller, Wire.ScopeId, null, 50, null)).GetProperty("assessmentComplete").GetBoolean(), "stale inventory never complete");
        Equal(false, Data(engine.OsSupport(known with { ValidationLimitations = ["SYNTHETIC_GAP"] },
            caller, Wire.ScopeId, null, 50, null)).GetProperty("assessmentComplete").GetBoolean(), "validation gaps never complete");
        Equal(0, Data(differentPolicy.OsSupport(ds, caller, Wire.ScopeId, "approaching", 50, null))
            .GetProperty("totalCount").GetInt32(), "configured 30-day threshold changes risk filter");
        Equal("2027-01-31", first.GetProperty("catalogValidThrough").GetString(), "catalog renewal deadline explicit");
        Equal(6, first.GetProperty("countsByAssessment").EnumerateArray().Sum(r => r.GetProperty("computerCount").GetInt32()),
            "phase summary accounts for every retained computer");
        var indexed = Data(engine.OsSupport(ds with { StorageMode = "indexed" }, caller, Wire.ScopeId, null, 2, null));
        Equal(first.ToString(), indexed.ToString(), "legacy and indexed views assess identical observed inventory");

        clock.Now = new(2027, 1, 31, 12, 0, 0, TimeSpan.Zero);
        Equal("fresh", Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 50, null)).GetProperty("catalogState").GetString(), "90th evidence day inclusive");
        clock.Now = clock.Now.AddDays(1);
        var stale = Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 50, null));
        Equal("stale", stale.GetProperty("catalogState").GetString(), "91st day stale");
        Equal(0, stale.GetProperty("classifiedComputerCount").GetInt32(), "stale dates not confident classifications");
        Equal(0, stale.GetProperty("approachingExtendedEndCount").GetInt32(), "stale results never populate approaching filter");
        Equal(5, stale.GetProperty("unknownComputerCount").GetInt32(), "mapped stale products counted unknown");
        clock.Now = new(2027, 1, 13, 7, 59, 59, TimeSpan.Zero);
        Equal("extended", Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 50, null)).GetProperty("rows")
            .EnumerateArray().Single(r => r.GetProperty("entityId").GetString() == device.EntityId)
            .GetProperty("assessment").GetString(), "Pacific final support day before midnight via real query");
        clock.Now = clock.Now.AddSeconds(1);
        Equal("ended", Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 50, null)).GetProperty("rows")
            .EnumerateArray().Single(r => r.GetProperty("entityId").GetString() == device.EntityId)
            .GetProperty("assessment").GetString(), "Pacific next day via real query");
        clock.Now = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);
        var before = Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 50, null));
        Equal("notYetVerified", before.GetProperty("catalogState").GetString(), "Pacific date before verification date");
        clock.Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        var midnight = Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 2, null));
        Equal("fresh", midnight.GetProperty("catalogState").GetString(), "Pacific midnight evidence boundary");
        clock.Now = new(2026, 11, 3, 7, 59, 0, TimeSpan.Zero);
        var preMidnight = Data(engine.OsSupport(ds, caller, Wire.ScopeId, null, 2, null));
        clock.Now = clock.Now.AddMinutes(2);
        Fault(() => engine.OsSupport(ds, caller, Wire.ScopeId, null, 2, preMidnight.GetProperty("nextCursor").GetString()),
            "INVALID_CURSOR", "Pacific date changes reject still-unexpired continuation");
        Console.WriteLine($"PASS: {assertions} OS lifecycle assertions; exact mappings, support boundaries, evidence freshness, ESU, complete counts and bound paging.");

        OsLifecycleRow Assess(Device d, DateOnly at) => OsLifecycleCatalog.Assess(d, at, true, 365);
        static JsonElement Data(Envelope e) => JsonSerializer.SerializeToElement(e.Data, Wire.Json);
        void Equal<T>(T expected, T actual, string label)
        {
            assertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Lifecycle {label}: expected {expected}, got {actual}");
        }
        void Fault(Action run, string code, string label)
        {
            try { run(); } catch (QueryFault ex) when (ex.Code == code) { assertions++; return; }
            throw new InvalidOperationException("Lifecycle expected fault: " + label);
        }
        void Invalid(Action run, string label)
        {
            try { run(); } catch (InvalidOperationException) { assertions++; return; }
            throw new InvalidOperationException("Lifecycle expected invalid configuration: " + label);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
