using System.Text;
using System.Text.Json;

namespace ScomSetup;

internal static class SelfTests
{
    internal static void Run()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);
        var bridge = new NoCallsBridge();
        using var form = new WizardForm(AppContext.BaseDirectory, bridge);
        _ = form.Handle;
        Require(form.IsHandleCreated);
        Require(form.ReadRequest("Status").PlanPath == "");
        form.UseWorkerPlan(true);
        Require(form.ReadRequest("Status").PlanPath == Path.Combine(form.ReadPlan().PrivatePath, "setup-plan.json"));
        form.SetField("privatePath", @"C:\approved-private");
        Require(form.ReadRequest("Status").PlanPath == @"C:\approved-private\setup-plan.json");
        form.SetField("planPath", @"C:\explicit-worker\setup-plan.json");
        form.SetField("privatePath", @"C:\other-private");
        Require(form.ReadRequest("Status").PlanPath == @"C:\explicit-worker\setup-plan.json");
        form.UseWorkerPlan(false);
        var defaults = form.ReadPlan();
        Require(defaults.ScopeKind == "group" && defaults.GroupId is null && !defaults.WholeManagementGroupApproved);
        foreach (var property in typeof(SetupPlan).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            string key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (key is "scopeKind" or "groupId" or "packagePath") continue;
            form.SetField(key, "test-" + key);
        }
        var plan = form.ReadPlan();
        Require(plan.SchemaVersion == 1 && plan.PackagePath == AppContext.BaseDirectory.TrimEnd('\\'));
        foreach (var property in typeof(SetupPlan).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            string key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (key is "scopeKind" or "groupId" or "packagePath") continue;
            Require((string?)property.GetValue(plan) == "test-" + key);
        }
        foreach (var name in new[] { "stateDirectory", "identityPath", "receiverSettingsPath",
            "workerSettingsPath", "planPath", "hostingBundlePath", "hostingBundleVersion", "hostingBundleSha256" })
            form.SetField(name, "test-" + name);
        var request = form.ReadRequest("Configure");
        Require(request.StateDirectory == "test-stateDirectory" && request.PlanPath == "");
        Require(form.ReadRequest("Status").PlanPath == "");
        form.UseWorkerPlan(true);
        Require(form.ReadRequest("Status").PlanPath == "test-planPath" &&
            form.ReadRequest("Connect").PlanPath == "test-planPath" &&
            form.ReadRequest("PublishWorker").PlanPath == "test-planPath" &&
            form.ReadRequest("Configure").PlanPath == "" &&
            form.ReadRequest("Prepare").PlanPath == "");
        form.UseWorkerPlan(false);
        Require(form.ReadRequest("Status").PlanPath == "");
        Require(request.IdentityPath == "test-identityPath" && request.ReceiverSettingsPath == "test-receiverSettingsPath" &&
            request.WorkerSettingsPath == "test-workerSettingsPath" && request.HostingBundlePath == "test-hostingBundlePath" &&
            request.HostingBundleVersion == "test-hostingBundleVersion" && request.HostingBundleSha256 == "test-hostingBundleSha256");
        Require(request.Approval is null && !request.InstallIis && !request.AcknowledgeRestart && request.Selection is null);
        request.Plan.WorkerAccount = "D\\üser \"quoted\"\r\nnext";
        request.Approval = "Exact summary with é and \r\nline two";
        var wire = SetupBridge.Encode(request);
        Require(wire.EndsWith('\n') && wire.Count(c => c == '\n') == 1);
        var bytes = new UTF8Encoding(false).GetBytes(wire);
        Require(!(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        var decoded = JsonSerializer.Deserialize<BridgeRequest>(bytes, SetupBridge.Json)!;
        Require(decoded.Plan.WorkerAccount == request.Plan.WorkerAccount && decoded.Approval == request.Approval);
        Require(!DisplayText.IsExactSummarySafe("bad\u202evalue") && !DisplayText.IsExactSummarySafe("bad\0value") &&
            DisplayText.IsExactSummarySafe(request.Approval));
        Require(DisplayText.Clean("a\u202eb\0c", 2) == "ab");
        string[] overrides = ["SCOM_SETUP_TEST_OVERRIDE", "ASPNETCORE_URLS", "DOTNET_ROOT", "PSExecutionPolicyPreference", "PSModulePath", "PATH"];
        var saved = overrides.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            foreach (var name in overrides) Environment.SetEnvironmentVariable(name, "untrusted-inherited-test-value");
            start = SetupBridge.StartInfo(AppContext.BaseDirectory);
            Require(overrides.Where(name => name != "PATH").All(name => !start.Environment.ContainsKey(name)));
            Require(!start.Environment["PATH"]!.Contains("untrusted-inherited-test-value"));
        }
        finally
        {
            foreach (var entry in saved) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
        Require(start.ArgumentList.SequenceEqual(new[] { "-NoProfile", "-File",
            Path.Combine(AppContext.BaseDirectory, @"setup\Invoke-ScomSetupGui.ps1") }));
        Require(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError &&
            !start.UseShellExecute && start.CreateNoWindow);
        Require(start.Environment["PATH"] == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32") +
            ";" + Environment.GetFolderPath(Environment.SpecialFolder.Windows) + ";" +
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        Require(!start.Environment.ContainsKey("DOTNET_ROOT") && !start.Environment.ContainsKey("PSModulePath") &&
            !start.Environment.ContainsKey("PSExecutionPolicyPreference") &&
            !start.Environment.Keys.Any(k => k.StartsWith("SCOM", StringComparison.OrdinalIgnoreCase)));
        Require(start.StandardInputEncoding is UTF8Encoding);
        var handoff = WizardForm.WorkerHandoffStartInfo(@"DOMAIN\sp_service", Path.Combine(AppContext.BaseDirectory, "ScomSetup.exe"));
        Require(handoff.FileName == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "runas.exe") &&
            handoff.ArgumentList.SequenceEqual(new[] { @"/user:DOMAIN\sp_service", "\"" + Path.Combine(AppContext.BaseDirectory, "ScomSetup.exe") + "\"" }) &&
            !handoff.UseShellExecute && !handoff.CreateNoWindow && !handoff.RedirectStandardInput && !handoff.RedirectStandardOutput &&
            !handoff.ArgumentList.Any(argument => argument.Contains("/savecred") || argument.Contains("/netonly")));
        bool rejected = false;
        try { WizardForm.WorkerHandoffStartInfo(@"bad account /savecred", Path.Combine(AppContext.BaseDirectory, "ScomSetup.exe")); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected);
        form.ApplyPlan(new SetupPlan { ScopeKind = "managementGroup", WholeManagementGroupApproved = true });
        Require(form.ReadPlan().ScopeKind == "managementGroup" && form.ReadPlan().GroupId is null &&
            !form.ReadPlan().WholeManagementGroupApproved);
        Require(form.ReadRequest("Prepare").Selection == "ALL" &&
            !form.ReadRequest("Prepare").Plan.WholeManagementGroupApproved);
        Require(form.ReadRequest("Prepare", wholeApproved: true).Plan.WholeManagementGroupApproved);
        const string groupId = "33333333-3333-4333-8333-333333333333";
        form.ApplyPlan(new SetupPlan { ScopeKind = "group", GroupId = groupId, PackagePath = @"C:\untrusted-root" });
        Require(form.ReadPlan().GroupId == groupId && form.ReadRequest("Prepare").Selection == groupId &&
            !form.ReadPlan().WholeManagementGroupApproved && form.ReadPlan().PackagePath != @"C:\untrusted-root");
        Require(bridge.Calls == 0);
        // The form's constructor and handle creation use NoCallsBridge, so any startup action fails this test.
        TestWorkflow();
        TestSequentialBootstrap();
        TestWorkerWithBlockedCatalog();
    }

    private static void TestWorkflow()
    {
        var responses = new Queue<BridgeResponse>();
        var bridge = new RecordingBridge(() => Task.FromResult(responses.Dequeue()));
        using var form = new WizardForm(AppContext.BaseDirectory, bridge);
        _ = form.Handle;
        form.EffectDecision = (_, _) => throw new InvalidOperationException("Unexpected effect dialog");
        form.WholeScopeDecision = _ => throw new InvalidOperationException("Unexpected scope dialog");
        form.SetField("operationsManagerModule", @"C:\approved\OperationsManager.psd1");
        responses.Enqueue(new BridgeResponse { Ok = true, Code = "SETUP_GUI_PREFLIGHT_PASSED",
            Checks = [new BridgeCheck { Name = "SDK installed", Passed = true }] });
        Complete(form.RunActionAsync("Preflight"));
        Require(bridge.Requests.Count == 1 && bridge.Requests[0].Plan.GroupId is null &&
            bridge.Requests[0].Approval is null);

        form.SetField("planPath", @"C:\private\setup-plan.json");
        form.UseWorkerPlan(true);
        responses.Enqueue(new BridgeResponse { Ok = true, Code = "SETUP_GUI_WORKER_PLAN_READY",
            Plan = new SetupPlan { WorkerAccount = @"DOMAIN\worker", PackagePath = @"C:\different-package" } });
        Complete(form.RunActionAsync("Status"));
        Require(bridge.Requests[^1].PlanPath == @"C:\private\setup-plan.json" &&
            form.StatusText.Contains("Host journal was not read") && form.ReadPlan().WorkerAccount == @"DOMAIN\worker" &&
            form.ReadPlan().PackagePath == AppContext.BaseDirectory.TrimEnd('\\'));
        form.UseWorkerPlan(false);
        responses.Enqueue(new BridgeResponse { Ok = true, Stage = "prepared" });
        Complete(form.RunActionAsync("Status"));
        Require(bridge.Requests[^1].PlanPath == "");

        responses.Enqueue(new BridgeResponse { Ok = false, ApprovalRequired = true, Summary = "First exact effect é" });
        int decisions = 0;
        form.EffectDecision = (summary, previous) => { decisions++; Require(summary == "First exact effect é" && !previous); return false; };
        Complete(form.RunActionAsync("Prepare"));
        Require(decisions == 1 && bridge.Requests.Count == 4 && bridge.Requests[^1].Approval is null && !form.IsBusy);

        responses.Enqueue(new BridgeResponse { Ok = false, ApprovalRequired = true, Summary = "First exact effect é" });
        responses.Enqueue(new BridgeResponse { Ok = false, ApprovalRequired = true, Summary = "Second independent effect" });
        responses.Enqueue(new BridgeResponse { Ok = true, Code = "SETUP_GUI_JOURNAL_READY" });
        form.EffectDecision = (summary, previous) =>
        {
            decisions++;
            Require(previous ? summary == "Second independent effect" : summary == "First exact effect é");
            return true;
        };
        int start = bridge.Requests.Count;
        Complete(form.RunActionAsync("InstallPrerequisites"));
        Require(bridge.Requests.Count == start + 3 && bridge.Requests[start].Approval is null &&
            bridge.Requests[start + 1].Approval == "First exact effect é" &&
            bridge.Requests[start + 2].Approval == "Second independent effect" &&
            form.StatusText.Contains("again"));
        foreach (var request in bridge.Requests.Skip(start))
        {
            request.Approval = null;
            Require(SetupBridge.Encode(request) == SetupBridge.Encode(bridge.Requests[start]));
        }

        form.ApplyPlan(new SetupPlan { ScopeKind = "managementGroup", WholeManagementGroupApproved = true });
        form.WholeScopeDecision = _ => { decisions++; return false; };
        start = bridge.Requests.Count;
        Complete(form.RunActionAsync("Prepare"));
        Require(bridge.Requests.Count == start);
        var ordering = new List<string>();
        form.WholeScopeDecision = _ => { ordering.Add("scope"); return true; };
        form.EffectDecision = (_, _) => { ordering.Add("effect"); return false; };
        responses.Enqueue(new BridgeResponse { Ok = false, ApprovalRequired = true, Summary = "Combined Prepare effect" });
        Complete(form.RunActionAsync("Prepare"));
        Require(ordering.SequenceEqual(new[] { "scope", "effect" }) &&
            bridge.Requests[^1].Plan.WholeManagementGroupApproved && bridge.Requests[^1].Approval is null);

        // A terminal false response must never resubmit merely because a summary is present.
        form.ApplyPlan(new SetupPlan());
        responses.Enqueue(new BridgeResponse { Ok = false, Summary = "Not an approval request" });
        form.EffectDecision = (_, _) => throw new InvalidOperationException("Unexpected effect dialog");
        start = bridge.Requests.Count;
        Complete(form.RunActionAsync("Recover"));
        Require(bridge.Requests.Count == start + 1 && !form.IsBusy);

        var pending = new TaskCompletionSource<BridgeResponse>();
        bridge.Next = () => pending.Task;
        int notices = 0;
        form.BusyCloseNotice = () => notices++;
        var work = form.RunActionAsync("Recover");
        Require(form.IsBusy);
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        form.ClosingWhileBusy(form, closing);
        Require(closing.Cancel && notices == 1 && form.IsBusy);
        start = bridge.Requests.Count;
        Complete(form.RunActionAsync("Recover"));
        Require(bridge.Requests.Count == start);
        pending.SetException(new InvalidOperationException("sensitive exception must never display"));
        Complete(work);
        Require(!form.IsBusy && !form.StatusText.Contains("sensitive") &&
            form.StatusText.Contains("private journal"));
        var idleClosing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        form.ClosingWhileBusy(form, idleClosing);
        Require(!idleClosing.Cancel);
    }

    private static void TestSequentialBootstrap()
    {
        var bridge = new RecordingBridge(() => Task.FromResult(new BridgeResponse
        {
            Ok = false, Checks = [
                new BridgeCheck { Name = "Windows PowerShell", Passed = true },
                new BridgeCheck { Name = ".NET server runtime", Passed = false, Remedy = "Install the reviewed server runtime." }]
        }));
        using var form = new WizardForm(AppContext.BaseDirectory, bridge);
        _ = form.Handle;
        Require(bridge.Requests.Count == 0 && form.CurrentPage == 0 && !form.CanInstall && form.CanMoveNext);
        form.MovePage(1);
        Require(form.CurrentPage == 1);
        form.MovePage(-1);
        Require(form.CurrentPage == 0);
        form.Show();
        Application.DoEvents();
        form.Hide();
        Complete(form.OnFirstShownAsync());
        Require(bridge.Requests.Count == 1 && bridge.Requests[0].Action == "Bootstrap" &&
            bridge.Requests[0].Approval is null);
        Require(form.CheckColors.SequenceEqual(new[] { Color.DarkGreen, Color.Firebrick }) && !form.CanInstall);
        Complete(form.RunActionAsync("Prepare"));
        Require(bridge.Requests.Count == 1);
        form.SetField("workerAccount", @"DOMAIN\edited");
        var defaults = new SetupPlan { WorkerAccount = @"DOMAIN\must-not-overwrite", InstallPath = @"C:\suggested\install" };
        form.RenderResponse(new BridgeResponse { Ok = true, Defaults = defaults, Checks = [] }, "Bootstrap");
        Require(!form.CanInstall && form.CheckColors.Length == 0 &&
            form.ReadPlan().WorkerAccount == @"DOMAIN\edited");
        form.RenderResponse(new BridgeResponse { Ok = true, Checks = [new BridgeCheck { Name = "Actual probe", Passed = true }] }, "Bootstrap");
        Require(form.CanInstall && form.CanMoveNext);
        const string discoveredManagementGroup = "44444444-4444-4444-8444-444444444444";
        form.RenderResponse(new BridgeResponse { Ok = true, ManagementGroupId = discoveredManagementGroup,
            Groups = [new GroupItem { Id = "55555555-5555-4555-8555-555555555555", DisplayName = "Approved group" }] }, "Discover");
        Require(form.ReadPlan().ManagementGroupId == discoveredManagementGroup && form.ReadPlan().GroupId is null);
        form.MovePage(2); Require(form.CurrentPage == 0);
        form.MovePage(1); Require(form.CurrentPage == 1);
        form.SetLogRoot(@"C:\approved-logs");
        Require(form.ReadPlan().McpLogPath == @"C:\approved-logs\ScomMcp-McpLogs" &&
            form.ReadPlan().CollectorLogPath == @"C:\approved-logs\ScomMcp-CollectorLogs");
        form.MovePage(1); Require(form.CurrentPage == 2);
        form.SelectSiteForTest(new SiteOption { Name = "Existing", Applications = ["/other"],
            HttpsBindings = [new HttpsBinding { Host = "scom.example.invalid", Port = 443 }] }, ["ScomMcp-scom"]);
        Require(form.ReadPlan().SiteName == "Existing" && form.ReadPlan().ApplicationPath == "/scom" &&
            form.ReadPlan().PoolName == "ScomMcp-scom-2" &&
            form.ReadPlan().HttpsBaseUrl == "https://scom.example.invalid/scom");
        form.SelectSiteForTest(new SiteOption { Name = "Existing", Applications = ["/scom"] }, []);
        Require(form.ReadPlan().ApplicationPath == "");
        Require(!form.EvidenceInputsPresent);
        int calls = bridge.Requests.Count;
        Complete(form.RunActionAsync("Configure"));
        Complete(form.RunActionAsync("Activate"));
        Require(bridge.Requests.Count == calls);
        form.MovePage(-1); Require(form.CurrentPage == 1);
        form.ApplyPlan(new SetupPlan { InstallPath = @"C:\resumed\install", WorkerAccount = @"DOMAIN\resumed" });
        form.RenderResponse(new BridgeResponse { Defaults = defaults, Ok = true,
            Checks = [new BridgeCheck { Passed = true }] }, "Bootstrap");
        Require(form.ReadPlan().InstallPath == @"C:\resumed\install" &&
            form.ReadPlan().WorkerAccount == @"DOMAIN\resumed");
        bridge.Next = () => throw new InvalidOperationException("Simulated policy failure");
        Complete(form.RunActionAsync("Bootstrap"));
        Require(!form.CanInstall);

        using var defaultsForm = new WizardForm(AppContext.BaseDirectory, new NoCallsBridge());
        defaultsForm.SetField("workerAccount", @"DOMAIN\keep-edited");
        defaultsForm.RenderResponse(new BridgeResponse { Ok = true,
            Defaults = new SetupPlan { InstallPath = @"C:\fresh\install", OperationsManagerModule = @"C:\SCOM\OperationsManager.psd1",
                WorkerAccount = @"DOMAIN\do-not-apply", McpLogPath = @"C:\backend-mcp", CollectorLogPath = @"C:\backend-collector" },
            StateDirectory = @"C:\fresh\journal",
            Checks = [new BridgeCheck { Name = "Actual metadata probe", Passed = true }] }, "Bootstrap");
        Require(defaultsForm.ReadPlan().InstallPath == @"C:\fresh\install" &&
            defaultsForm.ReadPlan().OperationsManagerModule == @"C:\SCOM\OperationsManager.psd1" &&
            defaultsForm.ReadRequest("Prepare").StateDirectory == @"C:\fresh\journal" &&
            defaultsForm.ReadPlan().WorkerAccount == @"DOMAIN\keep-edited");
        Require(defaultsForm.ReadPlan().McpLogPath == Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScomMcp-McpLogs") &&
            defaultsForm.ReadPlan().CollectorLogPath == Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScomMcp-CollectorLogs"));
        defaultsForm.RenderResponse(new BridgeResponse { Ok = true, Defaults = new SetupPlan { InstallPath = @"C:\other\install" },
            Checks = [new BridgeCheck { Passed = true }] }, "Bootstrap");
        Require(defaultsForm.ReadPlan().InstallPath == @"C:\fresh\install");
    }

    private static void Complete(Task task)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Require(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void TestWorkerWithBlockedCatalog()
        {
            var responses = new Queue<BridgeResponse>();
            var bridge = new RecordingBridge(() => Task.FromResult(responses.Dequeue()));
            using var form = new WizardForm(AppContext.BaseDirectory, bridge);
            _ = form.Handle;
            form.BusyCloseNotice = () => { };
            responses.Enqueue(new BridgeResponse { Ok = false, Checks = [
                new BridgeCheck { Name = "Local OperationsManager module manifest", Passed = true },
                new BridgeCheck { Name = "Local IIS/certificate discovery", Passed = false, Remedy = "Use an authorized host session for IIS." }] });
            Complete(form.OnFirstShownAsync());
            Require(!form.CanInstall && form.CheckColors.Contains(Color.Firebrick));
            form.SetField("privatePath", @"C:\installed-private");
            form.SetField("stateDirectory", "");
            form.UseWorkerPlan(true);
            var plan = new SetupPlan { PrivatePath = @"C:\installed-private", WorkerAccount = @"DOMAIN\worker" };
            responses.Enqueue(new BridgeResponse { Ok = true, Code = "SETUP_GUI_WORKER_PLAN_READY", Plan = plan });
            Complete(form.RunActionAsync("Status"));
            Require(bridge.Requests[^1].Action == "Status" &&
                bridge.Requests[^1].PlanPath == @"C:\installed-private\setup-plan.json" &&
                bridge.Requests[^1].StateDirectory == "" && !form.CanInstall &&
                form.StatusText.Contains("Host journal was not read"));
            responses.Enqueue(new BridgeResponse { Ok = false, Code = "SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION" });
            int decisions = 0;
            form.EffectDecision = (_, _) => { decisions++; return true; };
            int before = bridge.Requests.Count;
            Complete(form.RunActionAsync("Connect"));
            Require(bridge.Requests.Count == before + 1 && decisions == 0 &&
                form.StatusText.Contains("exact configured worker"));
            responses.Enqueue(new BridgeResponse { Ok = false, ApprovalRequired = true, Summary = "Explicit bounded worker connection only." });
            responses.Enqueue(new BridgeResponse { Ok = true, Code = "SETUP_GUI_WORKER_ACTION_COMPLETE" });
            Complete(form.RunActionAsync("Connect"));
            Require(decisions == 1 && bridge.Requests[^1].Approval == "Explicit bounded worker connection only.");
            responses.Enqueue(new BridgeResponse { Ok = false, ApprovalRequired = true, Summary = "ONE bounded publication attempt." });
            responses.Enqueue(new BridgeResponse { Ok = true, Code = "SETUP_GUI_WORKER_ACTION_COMPLETE" });
            Complete(form.RunActionAsync("PublishWorker"));
            Require(decisions == 2 && bridge.Requests[^1].Approval == "ONE bounded publication attempt.");
            Require(bridge.Requests.Skip(1).All(request => request.StateDirectory == "" &&
                request.PlanPath == @"C:\installed-private\setup-plan.json"));
            before = bridge.Requests.Count;
            Complete(form.RunActionAsync("Prepare"));
            Require(bridge.Requests.Count == before && !form.CanInstall);
        }
    private sealed class RecordingBridge(Func<Task<BridgeResponse>> next) : ISetupBridge
    {
        internal Func<Task<BridgeResponse>> Next { get; set; } = next;
        internal List<BridgeRequest> Requests { get; } = [];
        public Task<BridgeResponse> InvokeAsync(BridgeRequest request)
        {
            Requests.Add(JsonSerializer.Deserialize<BridgeRequest>(SetupBridge.Encode(request), SetupBridge.Json)!);
            return Next();
        }
    }

    private static void Require(bool passed)
    {
        if (!passed) throw new InvalidOperationException("SETUP_GUI_SELF_TEST_FAILED");
    }
}
