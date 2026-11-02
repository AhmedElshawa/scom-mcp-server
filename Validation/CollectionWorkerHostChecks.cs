using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ScomMcp;

static class CollectionWorkerHostChecks
{
    public static async Task Run(string root)
    {
        var count = 0;
        var example = Path.GetFullPath(Path.Combine(root, "..", "..", "ScomMcp", "appsettings.Worker.example.json"));
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(example)))
        {
            var settings = document.RootElement.Deserialize<CollectionWorkerHostSettings>(Wire.Json)!;
            settings.Publisher.Validate();
            settings.Reconciliation.Validate();
            settings.Schedule.Validate();
            if (settings.Schedule.AttemptDeadline < TimeSpan.FromSeconds(settings.Profile.DeadlineSeconds +
                settings.Publisher.PublicationTimeoutSeconds))
                throw new InvalidOperationException("Example worker deadline cannot cover source plus publication.");
            count++;
        }
        await Reject(["--worker"]);
        await Reject(["--worker-once"]);
        await Reject(["--worker", Path.Combine(root, "does-not-exist.json")]);
        await Reject(["--worker-once", "relative.json"]);
        Console.WriteLine($"PASS: {count} worker host assertions; example budgets and real CLI rejects without starting MCP.");

        async Task Reject(string[] args)
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var directory = Path.GetDirectoryName(assembly)!;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), Path.Combine(directory, "ScomMcp.dll") }.Concat(args))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker CLI test could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var output = await stdout + await stderr;
                if (process.ExitCode != 1 || output.Length > 4096 ||
                    output.Contains("Now listening", StringComparison.OrdinalIgnoreCase) ||
                    args.Length == 2 && output.Contains(args[1], StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid worker CLI mode did not fail safely.");
                count++;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
