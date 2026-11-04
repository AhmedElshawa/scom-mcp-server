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
    private readonly Panel pages = new() { Dock = DockStyle.Fill };
    private readonly List<FlowLayoutPanel> pagePanels = [];
    private readonly List<string> pageTitles = [];
    private readonly Label heading = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 17, FontStyle.Bold) };
    private readonly FlowLayoutPanel checkResults = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly ComboBox sites = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 700 };
    private readonly ComboBox certificates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 700 };
    private readonly Label workerIdentity = new() { AutoSize = true, MaximumSize = new Size(800, 0) };
    private readonly Label evidence = new() { AutoSize = true, MaximumSize = new Size(800, 0) };
    private readonly Dictionary<string, List<Button>> actionButtons = new();
    private readonly Form advanced = new() { Text = "Advanced deployment settings", Width = 900, Height = 720,
        StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
    private readonly TextBox logRoot = new() { Width = 700 };
    private readonly Label logPaths = new() { AutoSize = true, MaximumSize = new Size(800, 0) };
    private readonly TextBox applicationName = new() { Width = 300, Text = "scom" };
    private int pageIndex;
    private bool shownStarted;
    private bool prerequisitesPassed;
    private bool actualChecksReceived;
    private string? deploymentStage;
    private bool sourceConnected;
    private bool updating;
    private bool defaultsApplied;
    private string[] existingPools = [];
    private string currentAccount = WindowsIdentity.GetCurrent().Name;
    private Dictionary<string, string> initialValues = [];
    private string? proposedWorkerPlanPath;
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
    private static readonly string[] Actions = ["Bootstrap", "Preflight", "Discover", "Prepare", "Configure", "Activate", "Status",
        "Recover", "InstallPrerequisites", "Connect", "PublishWorker", "Publish", "Accept"];

    internal WizardForm(string root, ISetupBridge setupBridge)
    {
        packageRoot = Path.GetFullPath(root).TrimEnd('\\');
        bridge = setupBridge;
        Text = "SCOM deployment setup";
        Width = 1040; Height = 830; MinimumSize = new Size(900, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 85));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(stage, 0, 1); layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(pages, 0, 3); layout.Controls.Add(log, 0, 4);
        var navigation = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var advancedButton = new Button { Text = "Advanced / resume…", AutoSize = true };
        advancedButton.Click += (_, _) => { advanced.ShowDialog(this); UpdateNavigation(); };
        navigation.Controls.AddRange([back, next, advancedButton, elevate]);
        layout.Controls.Add(navigation, 0, 5);
        Controls.Add(layout);

        var details = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        advanced.Controls.Add(details);
        advanced.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; advanced.Hide(); } };
        Note(details, "Proposed defaults are editable; no directory, account, certificate or external evidence is created by editing. " +
            "Existing deployments must be explicitly resumed. Host and exact-worker sessions are separate.");
        Field(details, "packagePath", "Reviewed package (fixed)", packageRoot, readOnly: true);
        Field(details, "stateDirectory", "Private journal directory (new or existing)", browse: "folder");
        Field(details, "planPath", "Installed private setup-plan.json (worker resume)", browse: "json");
        details.Controls.Add(workerPlanMode);
        ActionButton(details, "Load status / resume", "Status");
        foreach (var (key, label) in new (string, string)[]
        {
            ("installPath", "NEW installation directory"), ("privatePath", "NEW private worker configuration directory"),
            ("receiverStatePath", "NEW receiver database directory"), ("workerStatePath", "NEW worker checkpoint directory"),
            ("mcpLogPath", "NEW MCP log directory (local NTFS)"), ("collectorLogPath", "NEW collector log directory (separate local NTFS)")
        }) Field(details, key, label, browse: "folder");
        fields["privatePath"].TextChanged += (_, _) => ProposeWorkerPlanPath();

        Field(details, "siteName", "IIS site name (advanced override)");
        Field(details, "applicationPath", "NEW application path", "/scom");
        Field(details, "poolName", "NEW dedicated pool name");
        Field(details, "httpsBaseUrl", "Approved HTTPS base URL including application prefix");
        Field(details, "tlsThumbprint", "Existing LocalMachine My TLS certificate thumbprint");
        Field(details, "managementGroupId", "Approved management-group GUID");
        Field(details, "operationsManagerModule", "Approved installed OperationsManager.psd1", browse: "module");

        var welcome = Page("Check prerequisites");
        Note(welcome, "Setup checks this computer automatically. Green means a check actually passed; red items need attention. " +
            "This read-only check does not connect to SCOM, install software or request elevation.");
        welcome.Controls.Add(checkResults);
        Note(welcome, "Checking has not run yet. Script signing and Windows policy remain enforced.");
        ActionButton(welcome, "Check again", "Bootstrap");

        var logs = Page("Choose log folder");
        Note(logs, "Choose one EXISTING local NTFS parent folder. Two distinct NEW log destinations are proposed beneath it.");
        logs.Controls.Add(logRoot);
        var browseLogs = new Button { Text = "Choose folder…", AutoSize = true };
        browseLogs.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose an EXISTING local NTFS parent folder", ShowNewFolderButton = false };
            if (dialog.ShowDialog(this) == DialogResult.OK) logRoot.Text = dialog.SelectedPath;
        };
        logs.Controls.Add(browseLogs);
        logs.Controls.Add(logPaths);
        Note(logs, "The parent must already exist. These separate paths are proposed only; no folders are created until exact approval. Advanced can override them individually.");
        logRoot.TextChanged += (_, _) => DeriveLogs();
        foreach (var key in new[] { "mcpLogPath", "collectorLogPath" })
            fields[key].TextChanged += (_, _) =>
            {
                if (!updating) logRoot.Clear();
                UpdateLogPaths();
            };

        var host = Page("Choose web application");
        Note(host, "Select a discovered IIS site. Setup creates a NEW dedicated application and pool; it never reuses or overwrites the site's existing pool.");
        host.Controls.Add(sites);
        host.Controls.Add(new Label { Text = "New application name", AutoSize = true });
        host.Controls.Add(applicationName);
        details.Controls.Add(new Label { Text = "Discovered existing HTTPS certificates (explicit selection required)", AutoSize = true });
        details.Controls.Add(certificates);
        Note(host, "No compatible HTTPS site/certificate? Use Advanced for a reviewed new-site plan or correct IIS and check again.");
        sites.SelectedIndexChanged += (_, _) => SelectPlacement();
        applicationName.TextChanged += (_, _) => SelectPlacement();
        certificates.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && certificates.SelectedItem is CertificateOption certificate)
                fields["tlsThumbprint"].Text = certificate.Thumbprint;
        };

        var worker = Page("Choose worker account");
        Note(worker, "Enter the existing service account approved for read-only SCOM access, for example DOMAIN\\sp_service. " +
            "This account controls the worker configuration and ACLs; it is not the IIS application-pool identity.");
        worker.Controls.Add(workerIdentity);
        Field(worker, "workerAccount", @"Existing worker DOMAIN\account (no password)");
        Note(worker, "Setup never captures credentials or impersonates this account. For worker actions, sign in through an approved Windows session, " +
            "or Shift+right-click ScomSetup.exe → Run as different user, and enter credentials only in the Windows prompt. " +
            "Then use Advanced to load the installed private setup-plan.json. The bridge verifies the exact current worker account.");
        fields["workerAccount"].TextChanged += (_, _) => { sourceConnected = false; UpdateWorkerIdentity(); };
        var handoff = new Button { Text = "Open setup as selected worker (Windows RunAs)…", AutoSize = true };
        handoff.Click += async (_, _) => await OpenWorkerSessionAsync();
        worker.Controls.Add(handoff);
        var selectWorkerPlan = new Button { Text = "Load installed worker plan (read-only)…", AutoSize = true };
        selectWorkerPlan.Click += async (_, _) =>
        {
            ProposeWorkerPlanPath(); workerPlanMode.Checked = true;
            await RunActionAsync("Status");
        };
        worker.Controls.Add(selectWorkerPlan);

        var scope = Page("Choose SCOM scope");
        Field(scope, "managementServer", "SCOM management server FQDN");
        fields["managementServer"].TextChanged += (_, _) =>
        {
            if (updating) return;
            sourceConnected = false; loadedGroupId = null; groups.Items.Clear();
            fields["managementGroupId"].Clear();
        };
        Note(scope, "Choose an existing group after separately approving a bounded discovery read. No members or inventory are collected.");
        ActionButton(scope, "Discover existing groups…", "Discover");
        scope.Controls.Add(groups); scope.Controls.Add(wholeWindows);
        wholeWindows.CheckedChanged += (_, _) => groups.Enabled = !wholeWindows.Checked;
        groups.SelectedIndexChanged += (_, _) => { if (groups.SelectedItem is GroupItem item) loadedGroupId = item.Id; };
        Note(scope, "Whole-management-group includes all supported Windows inventory, not every SCOM class. It requires its own separate confirmation.");

        var prerequisites = details;
        Note(prerequisites, "Optional local installation: independently obtain and review the Microsoft Hosting Bundle. " +
            "Supply the approved exact version and SHA256; setup does not download it or claim publisher trust. " +
            "Policy/signature requirements remain enforced. Restart the machine yourself when required; this app never reboots it.");
        prerequisites.Controls.Add(installIis);
        Field(prerequisites, "hostingBundlePath", "Verified LOCAL Hosting Bundle installer", browse: "exe");
        Field(prerequisites, "hostingBundleVersion", "Approved exact Hosting Bundle version");
        Field(prerequisites, "hostingBundleSha256", "Approved SHA256 (64 hex characters)");
        prerequisites.Controls.Add(restartAcknowledged);
        ActionButton(prerequisites, "Install reviewed prerequisites…", "InstallPrerequisites");

        var prepare = Page("Review deployment and evidence");
        prepare.Controls.Add(evidence);
        Note(prepare, "Prepare stages new files only. Configure/activation stay blocked until all three externally reviewed JSON inputs are selected in Advanced. " +
            "Setup cannot create or certify Entra consent, certificate access, intended-reader grants or source mappings.");
        ActionButton(prepare, "Recheck plan prerequisites", "Preflight");
        ActionButton(prepare, "Prepare new deployment…", "Prepare");
        Field(details, "identityPath", "Nonsecret Entra identity JSON", browse: "json");
        Field(details, "receiverSettingsPath", "Reviewed receiver settings JSON", browse: "json");
        Field(details, "workerSettingsPath", "Reviewed worker settings JSON", browse: "json");
        foreach (var key in new[] { "identityPath", "receiverSettingsPath", "workerSettingsPath" })
            fields[key].TextChanged += (_, _) => UpdateNavigation();
        var doneAdvanced = new Button { Text = "Done", AutoSize = true };
        doneAdvanced.Click += (_, _) => advanced.Hide();
        details.Controls.Add(doneAdvanced);
        Docs(prepare);
        ActionButton(prepare, "Import reviewed configuration…", "Configure");

        var publish = Page("Activate and publish");
        Note(publish, "Host: Activate starts only the owned HTTPS app after reviewed configuration. Worker: reopen this app as the exact approved " +
            "worker account (normal sign-in / approved session; no impersonation), load the installed private plan via Status, then Connect explicitly. " +
            "PublishWorker performs ONE separately approved bounded collection/publication/checkpoint attempt. No recurrence, metrics or live checks. " +
            "Host: Publish records the receipt only; it does not query inventory or certify reader acceptance.");
        ActionButton(publish, "Host: activate HTTPS app…", "Activate");
        ActionButton(publish, "Worker: connect / verify scope identity…", "Connect");
        ActionButton(publish, "Worker: one bounded publication…", "PublishWorker");
        ActionButton(publish, "Host: record worker receipt…", "Publish");

        var acceptance = Page("Acceptance and recovery");
        Note(acceptance, "Test intended-user Studio tool discovery and committed inventory, unauthorized-user denial, and scoped coverage externally. " +
            "Acceptance records operator attestation, NOT automated certification. A pending action blocks normal progression; " +
            "inspect Status and the private journal before recovery. Recover stops/disables only the owned pool, preserves files/databases/journal, " +
            "and does NOT roll back, clear partial work or automatically retry publication.");
        acceptance.Controls.Add(attestation);
        ActionButton(acceptance, "Record operator acceptance…", "Accept");
        ActionButton(acceptance, "Refresh status (read-only)", "Status");
        ActionButton(acceptance, "Recover: stop owned application…", "Recover");
        Docs(acceptance);

        back.Click += (_, _) => MovePage(-1);
        next.Click += (_, _) => MovePage(1);
        elevate.Click += (_, _) => RestartElevated();
        FormClosing += ClosingWhileBusy;
        FormClosed += (_, _) => advanced.Dispose();
        Shown += async (_, _) => await OnFirstShownAsync();
        ProposeDefaults();
        initialValues = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
        UpdateNavigation();
    }

    private FlowLayoutPanel Page(string title)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        pagePanels.Add(panel); pageTitles.Add(title);
        return panel;
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
        if (!actionButtons.TryGetValue(action, out var buttons)) actionButtons[action] = buttons = [];
        buttons.Add(button);
        page.Controls.Add(button);
    }

    internal async Task OnFirstShownAsync()
    {
        if (shownStarted) return;
        shownStarted = true;
        await RunActionAsync("Bootstrap");
    }

    private void ProposeDefaults()
    {
        string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        // Sibling directories satisfy the backend's non-overlap contract; creation is never automatic.
        foreach (var (key, suffix) in new[] { ("installPath", "Install"), ("privatePath", "Private"),
            ("receiverStatePath", "Receiver"), ("workerStatePath", "Worker"), ("stateDirectory", "Journal") })
            fields[key].Text = Path.Combine(data, "ScomMcp-" + suffix);
        fields["siteName"].Text = "ScomMcp";
        fields["poolName"].Text = "ScomMcp-Setup";
        logRoot.Text = data;
        UpdateWorkerIdentity();
    }

    internal void DeriveLogs()
    {
        if (updating || string.IsNullOrWhiteSpace(logRoot.Text)) return;
        try
        {
            updating = true;
            fields["mcpLogPath"].Text = Path.Combine(logRoot.Text.Trim(), "ScomMcp-McpLogs");
            fields["collectorLogPath"].Text = Path.Combine(logRoot.Text.Trim(), "ScomMcp-CollectorLogs");
        }
        catch { status.Text = "Choose a valid local log folder."; }
        finally { updating = false; }
        UpdateLogPaths();
    }

    private void UpdateLogPaths() => logPaths.Text =
        $"MCP logs (proposed): {DisplayText.Clean(fields["mcpLogPath"].Text, 500)}\n" +
        $"Collector logs (proposed): {DisplayText.Clean(fields["collectorLogPath"].Text, 500)}";

    internal void SetLogRoot(string path) => logRoot.Text = path;
    internal int CurrentPage => pageIndex;
    internal int PageCount => pagePanels.Count;
    internal bool CanInstall => actualChecksReceived && prerequisitesPassed;
    internal bool EvidenceInputsPresent => new[] { "identityPath", "receiverSettingsPath", "workerSettingsPath" }
        .All(key => !string.IsNullOrWhiteSpace(fields[key].Text) && File.Exists(fields[key].Text.Trim()));
    internal bool CanMoveNext => !busy && pageIndex < pagePanels.Count - 1 && (pageIndex switch
    {
        0 => true,
        1 => !string.IsNullOrWhiteSpace(fields["mcpLogPath"].Text) && !string.IsNullOrWhiteSpace(fields["collectorLogPath"].Text),
        2 => true,
        3 => fields["workerAccount"].Text.Contains('\\'),
        4 => wholeWindows.Checked || loadedGroupId is not null,
        _ => true
    });

    internal void MovePage(int direction)
    {
        if (busy || (direction != -1 && direction != 1) || (direction == 1 && !CanMoveNext)) return;
        int target = pageIndex + direction;
        if (target < 0 || target >= pagePanels.Count) return;
        pageIndex = target;
        UpdateNavigation();
    }

    private void UpdateWorkerIdentity()
    {
        string selected = DisplayText.Clean(fields["workerAccount"].Text, 160);
        workerIdentity.Text = $"Current Windows identity: {DisplayText.Clean(currentAccount, 160)}\n" +
            $"Selected worker: {(selected.Length == 0 ? "not selected" : selected)}\n" +
            "IIS runs in its separate dedicated ApplicationPoolIdentity. Worker Connect/Publish require the exact selected account.";
        UpdateNavigation();
    }

    internal void ProposeWorkerPlanPath()
    {
        string current = fields["planPath"].Text;
        if (current.Length > 0 && current != proposedWorkerPlanPath) return;
        try
        {
            string directory = fields["privatePath"].Text.Trim();
            proposedWorkerPlanPath = string.IsNullOrWhiteSpace(directory) ? "" : Path.Combine(directory, "setup-plan.json");
            fields["planPath"].Text = proposedWorkerPlanPath;
        }
        catch { /* Preserve the current path until the private directory is valid. */ }
    }

    internal static ProcessStartInfo WorkerHandoffStartInfo(string account, string executable)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(account, @"^[A-Za-z0-9._-]+\\[A-Za-z0-9._$-]+$"))
            throw new InvalidOperationException("WORKER_ACCOUNT_FORMAT");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "runas.exe"))
        {
            UseShellExecute = false, CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        info.ArgumentList.Add("/user:" + account);
        info.ArgumentList.Add("\"" + Path.GetFullPath(executable) + "\"");
        return info;
    }

    private async Task OpenWorkerSessionAsync()
    {
        if (busy) return;
        try
        {
            var start = WorkerHandoffStartInfo(fields["workerAccount"].Text.Trim(), Path.Combine(packageRoot, "ScomSetup.exe"));
            if (MessageBox.Show(this, "Open a separate setup session using Windows RunAs? Enter credentials ONLY in the Windows RunAs console. " +
                "This app never reads or saves them. The current window's identity does not change. " +
                "In the new window, load the installed private plan in Advanced before worker actions.",
                "Windows account handoff", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            using var process = Process.Start(start);
            if (process is null) return;
            SetBusy(true);
            status.Text = "Windows RunAs owns the credential prompt. This window's Windows identity is unchanged.";
            await process.WaitForExitAsync();
            status.Text = process.ExitCode == 0 ? "Windows RunAs completed. Use the new worker window if launched; this window's account is unchanged." :
                "Windows RunAs did not launch a worker session. Check the account and approved Windows sign-in/Secondary Logon policy.";
        }
        catch { status.Text = "Worker handoff unavailable. Use an approved Windows session as DOMAIN\\account. No credentials were captured."; }
        finally { SetBusy(false); }
    }

    internal void SelectSiteForTest(SiteOption site, string[] pools)
    {
        existingPools = pools; sites.Items.Clear(); sites.Items.Add(site); sites.SelectedIndex = 0;
    }

    private void SelectPlacement()
    {
        if (updating || sites.SelectedItem is not SiteOption site) return;
        string name = applicationName.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
        { status.Text = "Enter a new application name using letters, numbers, underscore or hyphen."; return; }
        string application = "/" + name;
        if ((site.Applications ?? []).Any(a => a.TrimEnd('/').Equals(application, StringComparison.OrdinalIgnoreCase)))
        {
            fields["applicationPath"].Text = "";
            status.Text = "That application already exists. Choose a NEW application name; existing apps and pools are never overwritten.";
            UpdateNavigation(); return;
        }
        fields["siteName"].Text = site.Name;
        fields["applicationPath"].Text = application;
        string pool = "ScomMcp-" + name;
        int suffix = 2;
        while (existingPools.Contains(pool, StringComparer.OrdinalIgnoreCase)) pool = "ScomMcp-" + name + "-" + suffix++;
        fields["poolName"].Text = pool;
        var binding = (site.HttpsBindings ?? []).FirstOrDefault(b => Uri.CheckHostName(b.Host) == UriHostNameType.Dns && b.Port is > 0 and < 65536);
        fields["httpsBaseUrl"].Text = binding is null ? "" :
            new UriBuilder("https", binding.Host, binding.Port, application).Uri.AbsoluteUri.TrimEnd('/');
        if (binding is null) status.Text = "This site has no usable DNS HTTPS binding. Review host/certificate configuration in Advanced.";
        UpdateNavigation();
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
        defaultsApplied = true;
        sourceConnected = false;
        updating = true;
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
        SyncLogRoot();
        updating = false;
        UpdateWorkerIdentity();
        // A resumed flag is not an approval for another effect.
    }

    internal async Task RunActionAsync(string action)
    {
        if (busy || !Actions.Contains(action)) return;
        if (action is "Prepare" or "Configure" or "Activate" && !CanInstall)
        {
            status.Text = "Blocked: every prerequisite must actually pass. Return to the first screen and check again.";
            return;
        }
        if (action is "Configure" or "Activate" && !EvidenceInputsPresent)
        {
            status.Text = "Blocked: select all three real externally reviewed JSON files in Advanced. No identity or source evidence is inferred.";
            return;
        }
        if (action == "Accept" && !attestation.Checked)
        {
            status.Text = "Blocked: complete external acceptance testing and select the attestation checkbox."; return;
        }
        bool wholeApproved = false;
        if (wholeWindows.Checked && action is not "Bootstrap" and not "Preflight" and not "Discover" and not "Status")
        {
            const string warning = "WARNING: this selects ALL supported Windows inventory in the management group, not every SCOM class. " +
                "Do you explicitly approve this expanded scope? This does not approve any installation or source action.";
            wholeApproved = WholeScopeDecision?.Invoke(warning) ?? MessageBox.Show(this, warning,
                "Distinct whole-management-group scope approval", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (!wholeApproved) { status.Text = "Scope approval declined. No action started."; return; }
        }
        var request = ReadRequest(action, wholeApproved);
        if (action is "Bootstrap" or "Preflight")
        {
            actualChecksReceived = false;
            prerequisitesPassed = false;
            checkResults.Controls.Clear();
            checkResults.Controls.Add(new Label { Text = "Checking local prerequisites…", AutoSize = true });
        }
        SetBusy(true);
        try
        {
            while (true)
            {
                status.Text = "Waiting for setup bridge. Do not close; accepted effects are not cancellable or automatically rolled back.";
                var response = await bridge.InvokeAsync(request);
                RenderResponse(response, action);
                if (!response.ApprovalRequired) break;
                if (action is "Bootstrap" or "Preflight" or "Status")
                    throw new InvalidOperationException("READONLY_ACTION_CANNOT_REQUEST_APPROVAL");
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
            if (action is "Bootstrap" or "Preflight") RenderChecks(new BridgeResponse());
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

    internal void RenderResponse(BridgeResponse response, string action)
    {
        if (action is "Bootstrap" or "Preflight") RenderChecks(response);
        if (action == "Bootstrap")
        {
            if (!defaultsApplied)
            {
                if (response.Defaults is not null)
                    foreach (var property in typeof(SetupPlan).GetProperties().Where(p => p.PropertyType == typeof(string)))
                    {
                        string key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
                        if (key is not "packagePath" and not "mcpLogPath" and not "collectorLogPath" && fields.TryGetValue(key, out var box) &&
                            initialValues.TryGetValue(key, out var original) && box.Text == original)
                        {
                            string proposed = DisplayText.Clean(property.GetValue(response.Defaults) as string, 32767);
                            if (!string.IsNullOrWhiteSpace(proposed)) box.Text = proposed;
                        }
                updating = true;
                SyncLogRoot();
                updating = false;
                    }
                if (fields["stateDirectory"].Text == initialValues["stateDirectory"] && !string.IsNullOrWhiteSpace(response.StateDirectory))
                    fields["stateDirectory"].Text = DisplayText.Clean(response.StateDirectory, 32767);
                defaultsApplied = response.Defaults is not null;
            }
            currentAccount = response.CurrentAccount ?? currentAccount;
            updating = true;
            sites.Items.Clear(); certificates.Items.Clear();
            existingPools = response.Pools ?? [];
            foreach (var site in (response.Sites ?? []).Take(200)) sites.Items.Add(site);
            foreach (var certificate in (response.Certificates ?? []).Take(200)) certificates.Items.Add(certificate);
            updating = false;
            if (sites.Items.Count == 0) sites.Items.Add("No IIS sites discovered — correct prerequisites or use reviewed Advanced settings");
            if (certificates.Items.Count == 0) certificates.Items.Add("No certificates discovered — not installed or unavailable");
            UpdateWorkerIdentity();
        }
        string[] stages = ["new", "prepared", "configured", "activated", "published", "accepted"];
        string current = stages.Contains(response.Stage) ? response.Stage! : "not reported";
        string pending = Actions.Contains(response.PendingAction) ? response.PendingAction! : "none / not reported";
        if (response.Stage is not null || action == "Status" && response.Ok) deploymentStage = response.Stage;
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
            if (Guid.TryParseExact(response.ManagementGroupId, "D", out var managementGroup) && managementGroup != Guid.Empty)
                fields["managementGroupId"].Text = managementGroup.ToString("D");
            groups.Items.Clear(); loadedGroupId = null;
            foreach (var group in (response.Groups ?? []).Take(500))
                if (Guid.TryParseExact(group.Id, "D", out var id) && id != Guid.Empty)
                    groups.Items.Add(new GroupItem { Id = id.ToString("D"), DisplayName = group.DisplayName });
            status.Text = "Discovery complete. Select an existing group explicitly; no inventory or installation was approved.";
        }
        if (action == "Connect" && response.Ok && !response.ApprovalRequired) sourceConnected = true;
        if (action == "Status" && response.Ok && !response.ApprovalRequired && response.Plan is not null)
        {
            ApplyPlan(response.Plan);
            Append("Explicit resume loaded reviewed plan fields. Package root remains fixed to this executable.");
        }
        UpdateNavigation();
    }

    private void RenderChecks(BridgeResponse response)
    {
        checkResults.Controls.Clear();
        var checks = response.Checks ?? [];
        actualChecksReceived = checks.Length > 0;
        prerequisitesPassed = response.Ok && actualChecksReceived && checks.All(check => check.Passed) && !response.ApprovalRequired;
        if (checks.Length == 0)
            checkResults.Controls.Add(new Label { Text = "NOT CHECKED — prerequisite results unavailable. Verify the signed package and Windows PowerShell policy.",
                ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(800, 0) });
        foreach (var check in checks.Take(100))
        {
            string name = DisplayText.Clean(check.Name, 140);
            var label = new Label { Text = $"{(check.Passed ? "PASS" : "BLOCKED")} — {name}",
                ForeColor = check.Passed ? Color.DarkGreen : Color.Firebrick, AutoSize = true, MaximumSize = new Size(800, 0) };
            checkResults.Controls.Add(label);
            if (!check.Passed)
                checkResults.Controls.Add(new Label { Text = DisplayText.Clean(check.Remedy, 500) is { Length: > 0 } remedy ?
                    remedy : "Review the approved prerequisite installation and maintenance guidance.",
                    AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(18, 0, 3, 10) });
        }

    }

    private void SyncLogRoot()
    {
        try
        {
            string? parent = Path.GetDirectoryName(fields["mcpLogPath"].Text);
            if (parent is not null && string.Equals(parent, Path.GetDirectoryName(fields["collectorLogPath"].Text), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fields["mcpLogPath"].Text).Equals("ScomMcp-McpLogs", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fields["collectorLogPath"].Text).Equals("ScomMcp-CollectorLogs", StringComparison.OrdinalIgnoreCase))
                logRoot.Text = parent;
            else logRoot.Clear();
        }
        catch { /* Invalid manually entered paths stay visible in Advanced for correction. */ }
    }

    internal Color[] CheckColors => checkResults.Controls.OfType<Label>().Where(label => label.Text.StartsWith("PASS") ||
        label.Text.StartsWith("BLOCKED")).Select(label => label.ForeColor).ToArray();

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
            "Blocked: obtain external Entra/source evidence and select all three reviewed JSON inputs in Advanced.",
        "SETUP_PREREQUISITES_BLOCKED" =>
            "Blocked: review the first-screen checks. Correct the named Windows Server, IIS, Hosting Bundle, SDK or restart prerequisites in Advanced.",
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
            "Blocked: complete the required machine restart yourself, then acknowledge restart in Advanced and explicitly rerun prerequisite installation.",
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
        if (pagePanels.Count == 0) return;
        if (pages.Controls.Count == 0 || !ReferenceEquals(pages.Controls[0], pagePanels[pageIndex]))
        {
            pages.Controls.Clear(); pages.Controls.Add(pagePanels[pageIndex]);
        }
        heading.Text = $"Step {pageIndex + 1} of {pagePanels.Count} · {pageTitles[pageIndex]}";
        back.Enabled = !busy && pageIndex > 0;
        next.Enabled = CanMoveNext;
        foreach (var entry in actionButtons)
            foreach (var button in entry.Value)
                button.Enabled = !busy && (entry.Key switch
                {
                    "Prepare" => CanInstall,
                    "Configure" or "Activate" => CanInstall && EvidenceInputsPresent,
                    "PublishWorker" => sourceConnected || workerPlanMode.Checked,
                    _ => true
                });
        if (fields.ContainsKey("workerSettingsPath"))
            evidence.Text = $"Placement: {DisplayText.Clean(fields["siteName"].Text, 80)}{DisplayText.Clean(fields["applicationPath"].Text, 80)}" +
                $"  ·  NEW pool: {DisplayText.Clean(fields["poolName"].Text, 80)}\n" +
                $"Worker: {DisplayText.Clean(fields["workerAccount"].Text, 160)}\n" +
                $"Prerequisites: {(CanInstall ? "passed actual checks" : "blocked / not checked")}\n" +
                $"External identity / receiver / worker JSON: {(EvidenceInputsPresent ? "files selected; backend review still required" : "MISSING — select reviewed files in Advanced")}\n" +
                $"Source connection: {(sourceConnected ? "explicit worker check completed; not source certification" : "not verified in this session")}\n" +
                $"Host stage: {deploymentStage ?? "unknown"} · No recurrence or live checks are enabled by this wizard.";
    }
    private void SetBusy(bool value)
    {
        busy = value; pages.Enabled = !value; advanced.Enabled = !value; elevate.Enabled = !value;
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
