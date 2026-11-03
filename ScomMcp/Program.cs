using ScomMcp;

if (args.Length > 0 && args[0] is "--validate-setup" or "--validate-setup-json")
{
    try
    {
        if (args[0] == "--validate-setup-json")
        {
            if (args.Length != 2) throw new InvalidOperationException("Setup JSON validation requires one path.");
            SetupConfigurationValidation.ValidateJson(args[1]);
            Console.WriteLine("SCOM_SETUP_JSON_VALID");
            return;
        }
        if (args.Length != 3) throw new InvalidOperationException("Setup validation requires server directory and worker configuration.");
        var ready = SetupConfigurationValidation.Validate(args[1], args[2]);
        Console.WriteLine(ready ? "SCOM_SETUP_CONFIGURATION_READY" : "SCOM_SETUP_SOURCE_EVIDENCE_REQUIRED");
        Environment.ExitCode = ready ? 0 : 2;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"SCOM_SETUP_CONFIGURATION_INVALID ErrorType={error.GetType().Name}");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] is "--worker" or "--worker-once")
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Worker mode requires one absolute configuration path.");
        Environment.ExitCode = 1;
        return;
    }
    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler stop = (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
    Console.CancelKeyPress += stop;
    try
    {
        Environment.ExitCode = await CollectionWorkerHost.RunAsync(args[1], args[0] == "--worker-once", shutdown.Token);
    }
    finally { Console.CancelKeyPress -= stop; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
var auth = builder.Configuration.GetSection("Auth").Get<AuthSettings>() ?? new AuthSettings();
var source = builder.Configuration.GetSection("Source").Get<SourceSettings>() ?? new SourceSettings();
var readModel = builder.Configuration.GetSection("ReadModel").Get<ReadModelSettings>() ?? new ReadModelSettings();
var lifecycle = builder.Configuration.GetSection("Lifecycle").Get<LifecycleSettings>() ?? new LifecycleSettings();
var targetedLive = builder.Configuration.GetSection("TargetedLive").Get<TargetedLiveSettings>() ?? new TargetedLiveSettings();
Hosting.AddScomServices(builder.Services, auth, source, readModel, lifecycle, targetedLive);
var app = builder.Build();
Hosting.MapScom(app);
app.Run();
