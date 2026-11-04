using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ScomSetup;

internal sealed class WizardForm : Form
{
    private readonly string packageRoot;
    private readonly ISetupBridge bridge;
    internal Func<string, bool>? WholeScopeDecision { get; set; }
    internal Func<string, bool, bool>? EffectDecision { get; set; }
    internal Action? BusyCloseNotice { get; set; }
    internal bool IsBusy => busy;
    internal string StatusText => status.Text;
    private readonly TabControl pages = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly ComboBox groups = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620 };
    private readonly CheckBox wholeWindows = new() { AutoSize = true, Text = "ALL supported Windows inventory in this management group (not every SCOM class)" };
    private readonly CheckBox installIis = new() { AutoSize = true, Text = "Install required IIS components (machine changes)" };
    private readonly CheckBox restartAcknowledged = new() { AutoSize = true, Text = "I have completed the required machine restart; resume prerequisite checks" };
    private readonly CheckBox attestation = new() { AutoSize = true, Text = "I attest: intended-user Studio/tool discovery, committed inventory, unauthorized-user denial and scoped coverage were tested." };
    private readonly CheckBox workerPlanMode = new() { AutoSize = true,
        Text = "Use selected installed worker plan for Status / worker actions (do not read host journal)" };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label status = new() { AutoSize = true, Text = "Not started. No source connection, inventory, configuration or elevation has occurred." };
    private readonly Label stage = new() { AutoSize = true, Text = "Stage: not loaded | Pending: none" };
    private readonly Button back = new() { Text = "Back", AutoSize = true };
    private readonly Button next = new() { Text = "Next", AutoSize = true };
    private readonly Button elevate = new() { Text = "Restart as administrator", AutoSize = true };
    private bool busy;
    private string? loadedGroupId;
    private static readonly string[] Actions = ["Preflight", "Discover", "Prepare", "Configure", "Activate", "Status",
        "Recover", "InstallPrerequisites", "Connect", "PublishWorker", "Publish", "Accept"];

    internal WizardForm(string root, ISetupBridge setupBridge)
    {
        packageRoot = Path.GetFullPath(root).TrimEnd('\\');
        bridge = setupBridge;
        Text = "SCOM deployment setup";
        Width = 1120; Height = 880; MinimumSize = new Size(920, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(stage, 0, 0); layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(pages, 0, 2); layout.Controls.Add(log, 0, 3);
        var navigation = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        navigation.Controls.AddRange([back, next, elevate]);
        layout.Controls.Add(navigation, 0, 4);
        Controls.Add(layout);

        var welcome = Page("1 · Plan and resume");
        Note(welcome, "Guided deployment for a reviewed complete package. Navigation never runs an action. " +
            "Preflight is read-only and needs no journal. Source discovery/connection and every effect require separate confirmation. " +
            "Use host actions as administrator and worker actions only as the exact approved worker identity. No credentials or impersonation.");
        Field(welcome, "packagePath", "Reviewed package (fixed)", packageRoot, readOnly: true);
        Field(welcome, "stateDirectory", "Private journal directory (new or existing)", browse: "folder");
        Field(welcome, "planPath", "Optional installed private setup-plan.json (worker resume)", browse: "json");
        welcome.Controls.Add(workerPlanMode);
        Note(welcome, "Resume explicitly loads Status; it does not scan directories. Worker resume can use the readable installed private plan, " +
            "without access to the administrator journal. Confirm paths before host actions.");
        ActionButton(welcome, "Load status / resume", "Status");
        foreach (var (key, label) in new (string, string)[]
        {
            ("installPath", "NEW installation directory"), ("privatePath", "NEW private worker configuration directory"),
            ("receiverStatePath", "NEW receiver database directory"), ("workerStatePath", "NEW worker checkpoint directory"),
            ("mcpLogPath", "NEW MCP log directory (local NTFS)"), ("collectorLogPath", "NEW collector log directory (separate local NTFS)")
        }) Field(welcome, key, label, browse: "folder");

        var host = Page("2 · Host and source");
        Field(host, "siteName", "New site / existing site for NEW subapplication");
        Field(host, "applicationPath", "Application path", "/");
        Field(host, "poolName", "NEW dedicated pool");
        Field(host, "httpsBaseUrl", "Approved HTTPS base URL including application prefix");
        Field(host, "tlsThumbprint", "Existing LocalMachine My TLS certificate thumbprint");
        Field(host, "workerAccount", @"Approved worker DOMAIN\account (no password)");
        Field(host, "managementServer", "SCOM management server FQDN");
        Field(host, "managementGroupId", "Approved management-group GUID");
        Field(host, "operationsManagerModule", "Approved installed OperationsManager.psd1", browse: "module");
        Note(host, "Preflight does not contact SCOM. Fix the reported Windows Server, IIS, Hosting Bundle, certificate, " +
            "SCOM console/SDK and path prerequisites before preparing. Do not copy unreviewed modules or disable execution policy.");
        ActionButton(host, "Run read-only preflight", "Preflight");

        var scope = Page("3 · Scope");
        Note(scope, "Discovery is a separately approved bounded source read, not inventory collection. " +
            "Run as an authorized identity. Select an existing group GUID; the default is no selection, never whole-management-group.");
        ActionButton(scope, "Discover existing groups…", "Discover");
        scope.Controls.Add(groups); scope.Controls.Add(wholeWindows);
        wholeWindows.CheckedChanged += (_, _) => groups.Enabled = !wholeWindows.Checked;
        groups.SelectedIndexChanged += (_, _) => { if (groups.SelectedItem is GroupItem item) loadedGroupId = item.Id; };
        Note(scope, "WARNING: whole-management-group scope expands to all supported Windows inventory. A separate Yes/No warning " +
            "must be accepted before Prepare, in addition to the exact installation effect confirmation. Neither a checkbox nor discovery approves installation.");

        var prerequisites = Page("4 · Prerequisites");
        Note(prerequisites, "Optional local installation: independently obtain and review the Microsoft Hosting Bundle. " +
            "Supply the approved exact version and SHA256; setup does not download it or claim publisher trust. " +
            "Policy/signature requirements remain enforced. Restart the machine yourself when required; this app never reboots it.");
        prerequisites.Controls.Add(installIis);
        Field(prerequisites, "hostingBundlePath", "Verified LOCAL Hosting Bundle installer", browse: "exe");
        Field(prerequisites, "hostingBundleVersion", "Approved exact Hosting Bundle version");
        Field(prerequisites, "hostingBundleSha256", "Approved SHA256 (64 hex characters)");
        prerequisites.Controls.Add(restartAcknowledged);
        ActionButton(prerequisites, "Install reviewed prerequisites…", "InstallPrerequisites");

        var prepare = Page("5 · Prepare and configure");
        Note(prepare, "Prepare creates only reviewed new deployment paths and a private resumable journal; no IIS startup or inventory. " +
            "Configure requires externally prepared Entra identity and reviewed receiver/worker JSON. Shape validation is NOT tenant consent, " +
            "key access, delegated reader grants or source-mapping verification. Keep source/live/metrics guards truthful.");
        ActionButton(prepare, "Prepare new deployment…", "Prepare");
        Field(prepare, "identityPath", "Nonsecret Entra identity JSON", browse: "json");
        Field(prepare, "receiverSettingsPath", "Reviewed receiver settings JSON", browse: "json");
        Field(prepare, "workerSettingsPath", "Reviewed worker settings JSON", browse: "json");
        Note(prepare, "Authorized Entra administrator: prepare separate API, publisher and reader identities, Scom.Read delegated consent, " +
            "Scom.Ingest publisher role, certificate/key access and immutable intended-reader grants. Source owner: review inventory mappings, " +
            "deadlines, disabled guards and bounded approved scope. Setup does not fabricate this evidence.");
        Docs(prepare);
        ActionButton(prepare, "Import reviewed configuration…", "Configure");

        var publish = Page("6 · Activate and publish");
        Note(publish, "Host: Activate starts only the owned HTTPS app after reviewed configuration. Worker: reopen this app as the exact approved " +
            "worker account (normal sign-in / approved session; no impersonation), load the installed private plan via Status, then Connect explicitly. " +
            "PublishWorker performs ONE separately approved bounded collection/publication/checkpoint attempt. No recurrence, metrics or live checks. " +
            "Host: Publish records the receipt only; it does not query inventory or certify reader acceptance.");
        ActionButton(publish, "Host: activate HTTPS app…", "Activate");
        ActionButton(publish, "Worker: connect / verify scope identity…", "Connect");
        ActionButton(publish, "Worker: one bounded publication…", "PublishWorker");
        ActionButton(publish, "Host: record worker receipt…", "Publish");

        var acceptance = Page("7 · Acceptance and recovery");
        Note(acceptance, "Test intended-user Studio tool discovery and committed inventory, unauthorized-user denial, and scoped coverage externally. " +
            "Acceptance records operator attestation, NOT automated certification. A pending action blocks normal progression; " +
            "inspect Status and the private journal before recovery. Recover stops/disables only the owned pool, preserves files/databases/journal, " +
            "and does NOT roll back, clear partial work or automatically retry publication.");
        acceptance.Controls.Add(attestation);
        ActionButton(acceptance, "Record operator acceptance…", "Accept");
        ActionButton(acceptance, "Refresh status (read-only)", "Status");
        ActionButton(acceptance, "Recover: stop owned application…", "Recover");
        Docs(acceptance);

        back.Click += (_, _) => { if (pages.SelectedIndex > 0) pages.SelectedIndex--; };
        next.Click += (_, _) => { if (pages.SelectedIndex < pages.TabCount - 1) pages.SelectedIndex++; };
        pages.SelectedIndexChanged += (_, _) => UpdateNavigation();
        elevate.Click += (_, _) => RestartElevated();
        FormClosing += ClosingWhileBusy;
        UpdateNavigation();
    }

    private FlowLayoutPanel Page(string title)
    {
        var tab = new TabPage(title);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        tab.Controls.Add(panel); pages.TabPages.Add(tab); return panel;
    }

    private static void Note(FlowLayoutPanel page, string text) => page.Controls.Add(
        new Label { Text = text, AutoSize = true, MaximumSize = new Size(830, 0), Margin = new Padding(3, 10, 3, 10) });

    private void Field(FlowLayoutPanel page, string key, string label, string value = "", bool readOnly = false, string? browse = null)
    {
        page.Controls.Add(new Label { Text = label, AutoSize = true });
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var box = new TextBox { Width = 700, Text = value, ReadOnly = readOnly, AccessibleName = label };
        fields.Add(key, box); line.Controls.Add(box);
        if (browse is not null)
        {
            var button = new Button { Text = "Browse…", AutoSize = true };
            button.Click += (_, _) =>
            {
                if (browse == "folder")
                {
                    using var dialog = new FolderBrowserDialog { Description = "Select directory (type a new path in the field if it does not exist)" };
                    if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                }
                else
                {
                    using var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false,
                        Filter = browse switch { "json" => "JSON files|*.json", "module" => "PowerShell module manifest|*.psd1", _ => "Windows executable|*.exe" } };
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        box.Text = dialog.FileName;
                        if (key == "planPath") workerPlanMode.Checked = true;
                    }
                }
            };
            line.Controls.Add(button);
        }
        page.Controls.Add(line);
    }

    private void ActionButton(FlowLayoutPanel page, string title, string action)
    {
        var button = new Button { Text = title, AutoSize = true, Margin = new Padding(3, 10, 3, 10) };
        button.Click += async (_, _) => await RunActionAsync(action);
        page.Controls.Add(button);
    }

    private void Docs(FlowLayoutPanel page)
    {
        foreach (var (label, relative) in new[] { ("Deployment and source/identity gates", "README.md"), ("Recovery and operational documentation", "docs") })
        {
            var link = new LinkLabel { Text = label, AutoSize = true };
            link.LinkClicked += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(packageRoot, relative)) { UseShellExecute = true }); }
                catch { status.Text = "Documentation not present. Consult the reviewed release repository README and docs."; }
            };
            page.Controls.Add(link);
        }
        var entra = new LinkLabel { Text = "Microsoft Entra app registration and permissions guidance", AutoSize = true };
        entra.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://learn.microsoft.com/entra/identity-platform/quickstart-register-app") { UseShellExecute = true }); }
            catch { status.Text = "Open Microsoft Learn Entra identity-platform documentation in your approved browser."; }
        };
        page.Controls.Add(entra);
    }

    internal void SetField(string name, string value) => fields[name].Text = value;
    internal void UseWorkerPlan(bool selected) => workerPlanMode.Checked = selected;
    internal SetupPlan ReadPlan(bool wholeApproved = false)
    {
        var plan = new SetupPlan();
        foreach (var property in typeof(SetupPlan).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            string key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (fields.TryGetValue(key, out var box)) property.SetValue(plan, box.Text.Trim());
        }
        plan.PackagePath = packageRoot;
        plan.ScopeKind = wholeWindows.Checked ? "managementGroup" : "group";
        plan.GroupId = wholeWindows.Checked ? null : loadedGroupId;
        plan.WholeManagementGroupApproved = wholeWindows.Checked && wholeApproved;
        return plan;
    }

    internal BridgeRequest ReadRequest(string action, bool wholeApproved = false) => new()
    {
        Action = action, Plan = ReadPlan(wholeApproved),
        StateDirectory = fields["stateDirectory"].Text.Trim(),
        PlanPath = workerPlanMode.Checked && action is "Status" or "Connect" or "PublishWorker" ?
            fields["planPath"].Text.Trim() : "",
        IdentityPath = fields["identityPath"].Text.Trim(), ReceiverSettingsPath = fields["receiverSettingsPath"].Text.Trim(),
        WorkerSettingsPath = fields["workerSettingsPath"].Text.Trim(),
        Selection = wholeWindows.Checked ? "ALL" : loadedGroupId,
        InstallIis = installIis.Checked, AcknowledgeRestart = restartAcknowledged.Checked,
        HostingBundlePath = fields["hostingBundlePath"].Text.Trim(), HostingBundleVersion = fields["hostingBundleVersion"].Text.Trim(),
        HostingBundleSha256 = fields["hostingBundleSha256"].Text.Trim()
    };

    internal void ApplyPlan(SetupPlan plan)
    {
        foreach (var property in typeof(SetupPlan).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            string key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (key != "packagePath" && fields.TryGetValue(key, out var box))
                box.Text = DisplayText.Clean(property.GetValue(plan) as string, 32767);
        }
        loadedGroupId = Guid.TryParseExact(plan.GroupId, "D", out var id) ? id.ToString("D") : null;
        groups.Items.Clear();
        if (loadedGroupId is not null)
        {
            groups.Items.Add(new GroupItem { Id = loadedGroupId, DisplayName = "Resumed approved group" });
            groups.SelectedIndex = 0;
        }
        wholeWindows.Checked = plan.ScopeKind == "managementGroup";
        // A resumed flag is not an approval for another effect.
    }

    internal async Task RunActionAsync(string action)
    {
        if (busy || !Actions.Contains(action)) return;
        if (action == "Accept" && !attestation.Checked)
        {
            status.Text = "Blocked: complete external acceptance testing and select the attestation checkbox."; return;
        }
        bool wholeApproved = false;
        if (wholeWindows.Checked && action is not "Preflight" and not "Discover" and not "Status")
        {
            const string warning = "WARNING: this selects ALL supported Windows inventory in the management group, not every SCOM class. " +
                "Do you explicitly approve this expanded scope? This does not approve any installation or source action.";
            wholeApproved = WholeScopeDecision?.Invoke(warning) ?? MessageBox.Show(this, warning,
                "Distinct whole-management-group scope approval", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (!wholeApproved) { status.Text = "Scope approval declined. No action started."; return; }
        }
        var request = ReadRequest(action, wholeApproved);
        SetBusy(true);
        try
        {
            while (true)
            {
                status.Text = "Waiting for setup bridge. Do not close; accepted effects are not cancellable or automatically rolled back.";
                var response = await bridge.InvokeAsync(request);
                RenderResponse(response, action);
                if (!response.ApprovalRequired) break;
                if (!DisplayText.IsExactSummarySafe(response.Summary))
                    throw new InvalidOperationException("UNSAFE_APPROVAL_SUMMARY");
                if (!ConfirmEffect(response.Summary!, request.Approval is not null))
                {
                    status.Text = "Further approval declined. Earlier accepted effects may have completed; review Status before continuing.";
                    Append("Approval declined; no further bridge invocation.");
                    break;
                }
                // Each exact summary is approved only after a fresh human decision, never pre-approved.
                request.Approval = response.Summary;
            }
        }
        catch
        {
            status.Text = "Setup communication failed. No raw diagnostics are shown. Review Status and the private journal before retrying.";
            Append("Bridge failed or returned an invalid response. Preserve the journal; do not assume rollback.");
        }
        finally { SetBusy(false); }
    }

    private bool ConfirmEffect(string summary, bool priorEffects)
    {
        if (EffectDecision is not null) return EffectDecision(summary, priorEffects);
        using var approval = new ApprovalDialog(summary, priorEffects);
        return approval.ShowDialog(this) == DialogResult.Yes;
    }

    private void RenderResponse(BridgeResponse response, string action)
    {
        string[] stages = ["new", "prepared", "configured", "activated", "published", "accepted"];
        string current = stages.Contains(response.Stage) ? response.Stage! : "not reported";
        string pending = Actions.Contains(response.PendingAction) ? response.PendingAction! : "none / not reported";
        stage.Text = $"Stage: {current} | Pending: {pending}";
        status.Text = response.ApprovalRequired ? "An exact effect approval is required; earlier effects may already have completed." :
            response.Ok ? response.Code == "SETUP_GUI_JOURNAL_READY" ?
                "Private prerequisite journal is ready. Click Install reviewed prerequisites again to review the next independent effect." :
                response.Code == "SETUP_GUI_WORKER_PLAN_READY" ?
                "Installed worker plan loaded under the exact worker identity. Host journal was not read; host stage and evidence are unknown." :
                "Requested action completed. Stage and receipt are not reader certification." : Remedy(response.Code);
        Append($"{action}: {(response.ApprovalRequired ? "approval required" : response.Ok ? "completed" : "blocked")}. Stage: {current}.");
        foreach (var check in (response.Checks ?? []).Take(100))
            Append($"{CheckDescription(check.Name)}: {(check.Passed ? "PASS" : "BLOCKED — review prerequisites page and approved host requirements")}.");
        if (action == "Discover" && response.Ok && !response.ApprovalRequired)
        {
            groups.Items.Clear(); loadedGroupId = null;
            foreach (var group in (response.Groups ?? []).Take(500))
                if (Guid.TryParseExact(group.Id, "D", out var id) && id != Guid.Empty)
                    groups.Items.Add(new GroupItem { Id = id.ToString("D"), DisplayName = group.DisplayName });
            status.Text = "Discovery complete. Select an existing group explicitly; no inventory or installation was approved.";
        }
        if (action == "Status" && response.Ok && !response.ApprovalRequired && response.Plan is not null)
        {
            ApplyPlan(response.Plan);
            Append("Explicit resume loaded reviewed plan fields. Package root remains fixed to this executable.");
        }
    }

    private static string Remedy(string? code) => code switch
    {
        "SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE" or "SETUP_ADMINISTRATOR_REQUIRED" =>
            "Blocked: host action requires administrator. Use the explicit restart button; UAC cancellation is safe.",
        "SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION" =>
            "Blocked: open setup in an approved session signed in as the exact configured worker. No impersonation is offered.",
        "SETUP_INTERRUPTED_ACTION_REQUIRES_RECOVERY" =>
            "Blocked: pending action needs private journal review. Use Status and documented recovery, not an automatic retry.",
        "SETUP_STAGE_ORDER" => "Blocked: inspect Status and complete the preceding guided stage; no stage was skipped.",
        "SETUP_PAUSED_IDENTITY_AND_REVIEWED_CONFIG_REQUIRED" =>
            "Blocked: obtain external Entra/source evidence and select all three reviewed JSON inputs on page 5.",
        "SETUP_PREREQUISITES_BLOCKED" =>
            "Blocked: run read-only Preflight; correct the named Windows Server, IIS, Hosting Bundle, SDK or restart checks on pages 2 and 4.",
        "SETUP_SCOPE_APPROVAL_INVALID" or "SETUP_SCOPE_GUID_INVALID" or "SETUP_APPROVED_GROUP_NOT_VISIBLE" =>
            "Blocked: explicitly discover/select an approved existing group and verify its management-group GUID, or separately approve whole-Windows scope.",
        "SETUP_IDENTITY_INVALID" or "SETUP_IDENTITY_GUID_INVALID" or "SETUP_IDENTITY_RESOURCE_OR_ROLE_COLLISION" =>
            "Blocked: the Entra identity JSON failed validation. Have its authorized owner review distinct API/publisher/reader IDs, scope, role and certificate.",
        "SETUP_REVIEWED_CONFIGURATION_SCOPE_OR_DISABLED_GUARDS" or "SETUP_IMMUTABLE_READER_GRANTS_REQUIRED" =>
            "Blocked: ask the source/identity owner to review matching scope, disabled live/metrics guards, source mappings and immutable intended-reader grants.",
        "SETUP_PLAN_CHANGED_REVIEW_REQUIRED" or "SETUP_CONFIGURATION_CHANGED_REVIEW_REQUIRED" =>
            "Blocked: installed plan/configuration changed. Preserve the journal and review deployment ownership and hashes before proceeding.",
        "SETUP_EXISTING_RECEIPT_REVIEW_REQUIRED" =>
            "Blocked: an existing worker receipt needs review. Do not automatically repeat publication; inspect Status and the private worker evidence.",
        "RESTART_REQUIRED" or "SETUP_RESTART_REQUIRED" =>
            "Blocked: complete the required machine restart yourself, then acknowledge restart on page 4 and explicitly rerun prerequisite installation.",
        "SETUP_GUI_BRIDGE_BLOCKED" =>
            "Blocked: check Windows PowerShell 5.1 and approved script signing policy. Use a properly signed reviewed package; do not bypass policy.",
        _ => "Blocked: review plan fields, prerequisites, external identity/source gates and private journal. No unsafe details are logged."
    };

    private static string CheckDescription(string? name) => name switch
    {
        "Windows PowerShell 5.1" => "Windows PowerShell 5.1",
        "Supported Windows Server family" => "Supported Windows Server family",
        ".NET and ASP.NET Core 8 runtimes" => ".NET and ASP.NET Core 8 runtimes (server prerequisites, not GUI runtime)",
        "IIS management tools" => "IIS management tools",
        "IIS web server and activation services installed" => "IIS web server and activation services",
        ".NET 8 Hosting Bundle and ANCM" => ".NET 8 Hosting Bundle and ANCM",
        "64-bit execution" => "64-bit execution",
        "Approved OperationsManager module exists" => "Approved OperationsManager module",
        "No detected pending reboot" => "Restart completion",
        _ => "Additional prerequisite"
    };

    private void Append(string text)
    {
        if (log.TextLength > 24000) log.Clear();
        log.AppendText(text + Environment.NewLine);
    }
    private void UpdateNavigation()
    {
        back.Enabled = !busy && pages.SelectedIndex > 0;
        next.Enabled = !busy && pages.SelectedIndex < pages.TabCount - 1;
    }
    private void SetBusy(bool value)
    {
        busy = value; pages.Enabled = !value; elevate.Enabled = !value;
        UseWaitCursor = value; UpdateNavigation();
    }
    internal void ClosingWhileBusy(object? sender, FormClosingEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        if (BusyCloseNotice is not null) { BusyCloseNotice(); return; }
        MessageBox.Show(this, "Setup is still running. Accepted effects cannot safely be cancelled; closing is deferred. " +
            "Wait for completion, then inspect Status. This is not a rollback.", "Setup operation in progress",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private void RestartElevated()
    {
        if (busy) return;
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        { status.Text = "This setup process already has administrator privileges."; return; }
        if (MessageBox.Show(this, "Restart setup as administrator? Unsaved form entries will not be transferred. " +
            "After restart, explicitly load Status to resume an existing journal. No action is approved by elevation.",
            "Restart setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = true, Verb = "runas", WorkingDirectory = packageRoot });
            if (child is not null) Close();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { status.Text = "Administrator restart cancelled. No action was started."; }
        catch { status.Text = "Administrator restart failed. No action was started."; }
    }
}

internal sealed class ApprovalDialog : Form
{
    internal ApprovalDialog(string exactSummary, bool priorEffects)
    {
        Text = "Review exact setup effect"; Width = 800; Height = 520;
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(740, 0),
            Text = priorEffects ? "Earlier approved effects may have completed. Approve ONLY the exact next effect below? No does not roll back prior work." :
                "Approve ONLY the exact effect below? No is the default. No automatic inventory or additional effects are approved." }, 0, 0);
        layout.Controls.Add(new TextBox { Text = exactSummary, ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Both, WordWrap = true, Dock = DockStyle.Fill }, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var yes = new Button { Text = "Yes — approve this effect", DialogResult = DialogResult.Yes, AutoSize = true };
        var no = new Button { Text = "No", DialogResult = DialogResult.No, AutoSize = true };
        buttons.Controls.AddRange([no, yes]); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout); CancelButton = no; AcceptButton = no; ActiveControl = no;
    }
}
