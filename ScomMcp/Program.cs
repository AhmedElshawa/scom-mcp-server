using ScomMcp;

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
