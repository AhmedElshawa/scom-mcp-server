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
