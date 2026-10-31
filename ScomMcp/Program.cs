using ScomMcp;

var builder = WebApplication.CreateBuilder(args);
var auth = builder.Configuration.GetSection("Auth").Get<AuthSettings>() ?? new AuthSettings();
var source = builder.Configuration.GetSection("Source").Get<SourceSettings>() ?? new SourceSettings();
Hosting.AddScomServices(builder.Services, auth, source);
var app = builder.Build();
Hosting.MapScom(app);
app.Run();
