using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScomSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 2 && args[0] == "--diagnose-bootstrap")
                return DiagnoseBootstrap(args[1]);
            if (args.Length == 3 && args[0] == "--decode-bootstrap-fixture")
                return DecodeFixture(args[1], args[2]);
            if (args.Length is 1 or 2 && args[0] is "--self-test" or "--smoke-readonly")
            {
                SelfTests.Run();
                if (args.Length == 2)
                    File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(new { ok = true, bridgeCalls = 0 }));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--bootstrap-probe")
            {
                var bridge = new NoCallsBridge();
                using var form = new WizardForm(AppContext.BaseDirectory, bridge);
                _ = form.Handle;
                var runtime = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
                var root = Path.GetFullPath(AppContext.BaseDirectory);
                var selfContained = runtime.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(new
                {
                    selfContained, framework = RuntimeInformation.FrameworkDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    uiLoaded = form.IsHandleCreated, runtimeDirectory = runtime, bridgeCalls = bridge.Calls
                }));
                return selfContained && form.IsHandleCreated ? 0 : 1;
            }
            if (args.Length != 0) return 2;
            Application.Run(new WizardForm(AppContext.BaseDirectory, new SetupBridge(AppContext.BaseDirectory)));
            return 0;
        }
        catch
        {
            // Test modes communicate failure via exit status; never dump exception/customer content.
            if (args.Length == 0)
                MessageBox.Show("Setup could not start. Verify the complete reviewed package is present.",
                    "SCOM setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int DiagnoseBootstrap(string reportPath)
    {
        string[] knownChecks = ["Windows PowerShell 5.1", "Supported Windows Server family",
            ".NET and ASP.NET Core 8 runtimes", "IIS management tools", "IIS web server and activation services installed",
            ".NET 8 Hosting Bundle and ANCM", "64-bit execution", "Local OperationsManager module manifest",
            "Local SCOM SDK assembly identity metadata", "No detected pending reboot",
            "Local IIS/certificate discovery", "Local prerequisite probe completion"];
        BridgeResponse response;
        BridgeFailureException? failure = null;
        try
        {
            response = new SetupBridge(AppContext.BaseDirectory).InvokeAsync(new BridgeRequest { Action = "Bootstrap" })
                .GetAwaiter().GetResult();
        }
        catch (BridgeFailureException error)
        {
            failure = error;
            response = new BridgeResponse { Code = error.Code };
        }
        catch
        {
            response = new BridgeResponse { Code = "SETUP_GUI_UNKNOWN_FAILURE" };
        }
        var code = BridgeDiagnostics.SafeCode(response.Code);
        using var output = new FileStream(Path.GetFullPath(reportPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new
        {
            action = "Bootstrap", ok = response.Ok, code, remedy = BridgeDiagnostics.Remedy(code), bridgeCalls = 1,
            contractField = failure?.ContractField, expectedType = failure?.ExpectedType, actualType = failure?.ActualType,
            checks = (response.Checks ?? []).Select(check => new
            {
                name = knownChecks.Contains(check.Name) ? check.Name : "Additional prerequisite",
                passed = check.Passed
            })
        });
        // A completed diagnostic with blocked prerequisites is not an installation failure or approval.
        return (response.Checks ?? []).Length > 0 ? 0 : 1;
    }

    private static int DecodeFixture(string inputPath, string reportPath)
    {
        var size = new FileInfo(inputPath).Length;
        if (size > 2 * 1024 * 1024) return 1;
        BridgeFailureException? failure = null;
        try { _ = SetupBridge.Decode(File.ReadAllText(inputPath), "", 0); }
        catch (BridgeFailureException error) { failure = error; }
        using var output = new FileStream(Path.GetFullPath(reportPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new
        {
            decoded = failure is null, bridgeCalls = 0, code = failure?.Code,
            contractField = failure?.ContractField, expectedType = failure?.ExpectedType, actualType = failure?.ActualType
        });
        return failure is null ? 0 : 1;
    }
}

internal sealed class NoCallsBridge : ISetupBridge
{
    public int Calls { get; private set; }
    public Task<BridgeResponse> InvokeAsync(BridgeRequest request)
    {
        Calls++;
        throw new InvalidOperationException("Bridge invocation forbidden in read-only tests.");
    }
}
