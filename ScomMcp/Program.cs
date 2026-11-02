using ScomMcp;

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
Hosting.AddScomServices(builder.Services, auth, source, readModel);
var app = builder.Build();
Hosting.MapScom(app);
app.Run();
