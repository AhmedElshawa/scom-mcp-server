using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using ScomMcp;

static class ManagementGroupSourceChecks
{
    public static async Task Run(string root)
    {
        const string publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var count = 0;
        foreach (var mode in new[] { "complete", "partial", "empty" })
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-File",
                Path.Combine(Directory.GetCurrentDirectory(), "Collector", "Validate-ScomManagementGroupScope.ps1"),
                "-BridgeMode", mode }) start.ArgumentList.Add(arg);
            var scan = await NormalizationProcess.RunAsync(start, Encoding.UTF8.GetBytes("{}"), 131072,
                TimeSpan.FromSeconds(60), CancellationToken.None);
            if (scan.ScopeKind != "managementGroup" || scan.GroupId is not null || !scan.MembershipVerified)
                throw new InvalidOperationException("Management-group source bridge lost scope provenance.");
            count++;
            var store = new ReadModelStore(new()
            {
                Enabled = true, DatabasePath = Path.Combine(root, $"mg-source-{mode}.db"),
                ManagementGroupId = scan.ManagementGroupId, ScopeKind = "managementGroup",
                PublisherObjectIds = [publisher]
            }, new() { SnapshotEnabled = false }, TimeProvider.System);
            var profile = new Profile { Scan = scan };
            var source = new ReconciledCollectionSource(profile, new(), TimeProvider.System);
            var prepared = await source.PrepareAsync(CancellationToken.None);
            store.Begin(publisher, prepared.SessionId);
            var sequence = 0;
            await foreach (var batch in prepared.Batches)
                store.Append(publisher, prepared.SessionId, sequence++, Encoding.UTF8.GetBytes(Wire.Encode(batch)));
            store.Commit(publisher, prepared.SessionId, Encoding.UTF8.GetBytes(Wire.Encode(prepared.Manifest)));
            var caller = new Caller("synthetic", ImmutableHashSet.Create(Wire.ScopeId));
            var page = store.Page(caller, "objects", null, 50, null);
            if (page.TotalCount != (mode == "complete" ? 3 : mode == "partial" ? 1 : 0) ||
                page.Manifest.ExpectedObjectCount != (mode == "empty" ? 0 : 3) ||
                page.Manifest.Inventory.Complete != (mode != "partial"))
                throw new InvalidOperationException("Management-group activation collapsed scope into discovery.");
            count++;
            await source.AcknowledgeAsync(prepared.SessionId, CancellationToken.None);
            profile.Scan = scan with { MembershipVerified = false };
            store.Begin(publisher, await source.ReserveSessionAsync(CancellationToken.None));
            try { await source.PrepareAsync(CancellationToken.None); throw new InvalidOperationException("Unverified scope prepared."); }
            catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE") { count++; }
            try { store.Page(caller, "objects", null, 50, null); throw new InvalidOperationException("Failed scope served old data."); }
            catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE") { count++; }
        }
        Console.WriteLine($"PASS: {count} management-group source bridge assertions; complete/partial/empty activation and invalidation.");
    }
    private sealed class Profile : IScomNormalizationProfile
    {
        public required NormalizedSourceScan Scan { get; set; }
        public Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken token) =>
            Task.FromResult(Scan);
    }
}
