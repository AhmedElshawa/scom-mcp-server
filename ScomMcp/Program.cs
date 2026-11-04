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

if (args.Length > 0 && args[0] == "--worker-stop-request")
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Worker stop request requires the exact runtime-state directory.");
        Environment.ExitCode = 1;
        return;
    }
    try
    {
        WorkerRuntimeState.RequestStop(args[1]);
        Console.WriteLine("Worker stop request recorded.");
        Environment.ExitCode = 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Worker stop request refused. Code=WORKER_STOP_REQUEST_REFUSED ErrorType={error.GetType().Name}");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--worker-rearm")
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Worker rearm requires the exact runtime-state directory.");
        Environment.ExitCode = 1;
        return;
    }
    try
    {
        WorkerRuntimeState.Rearm(args[1]);
        Console.WriteLine("Worker runtime state rearmed; inspect the preserved failure evidence before starting the task.");
        Environment.ExitCode = 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Worker rearm refused. Code=WORKER_REARM_REFUSED ErrorType={error.GetType().Name}");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] is "--worker" or "--worker-once")
{
    var once = args[0] == "--worker-once";
    var validArguments = args.Length == 2 ||
        !once && args.Length == 4 && args[2] == "--runtime-state-dir";
    if (!validArguments)
    {
        Console.Error.WriteLine("Worker mode requires an absolute configuration path and optional runtime state directory.");
        Environment.ExitCode = 1;
        return;
    }
    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler stop = (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
    Console.CancelKeyPress += stop;
    try
    {
        Environment.ExitCode = await CollectionWorkerHost.RunAsync(args[1], once, shutdown.Token,
            args.Length == 4 ? args[3] : null);
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
