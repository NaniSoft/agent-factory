using AgentFactory.App.Api;
using AgentFactory.App.Fleet;

var builder = WebApplication.CreateBuilder(args);

// One resolved options object: the data, clone and worktree roots are made
// absolute against the content root once, so every service reads the same
// answer and a relative "data" works from any current directory.
var fleet = builder.Configuration.GetSection(FleetOptions.SectionName).Get<FleetOptions>()
    ?? new FleetOptions();

string Resolve(string path) => string.IsNullOrWhiteSpace(path)
    ? path
    : Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, path));

fleet.DataDir = Resolve(fleet.DataDir);
fleet.CloneRoot = Resolve(string.IsNullOrWhiteSpace(fleet.CloneRoot) ? Path.Combine("data", "clones") : fleet.CloneRoot);
fleet.WorktreeRoot = Resolve(string.IsNullOrWhiteSpace(fleet.WorktreeRoot) ? Path.Combine("data", "worktrees") : fleet.WorktreeRoot);

Directory.CreateDirectory(fleet.DataDir);
Directory.CreateDirectory(fleet.CloneRoot);
Directory.CreateDirectory(fleet.WorktreeRoot);

builder.Services.AddSingleton(fleet);

// The two stores of record. The config store hot-reloads a hand edit; the run
// store is the dashboard's history and the only place run state lives.
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<FleetOptions>();
    return new RunStore(options.DataDir, options.PlaygroundRepo);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<FleetOptions>();
    return new ConfigStore(options.DataDir, options.PlaygroundRepo);
});

builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<OpenCodeEngine>();
builder.Services.AddSingleton<EngineResolver>();
builder.Services.AddSingleton<ModelHealthProbe>();
builder.Services.AddSingleton<OrgScanner>();
builder.Services.AddSingleton<TriageService>();
builder.Services.AddSingleton<CrossRepoRouter>();
builder.Services.AddSingleton<RunExecutor>();
builder.Services.AddSingleton<ParentExecutor>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<FleetStatusService>();

// Resolvable as a singleton and hosted from that instance, so the API can
// wake the loop after an enrollment or a settings save.
builder.Services.AddSingleton<FleetRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetRunner>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync("""{"error":"the fleet hit an unhandled error"}""");
    }));
}

// The React app's static export at the root, beside the JSON surface. The
// export is copied into wwwroot by the build target in the csproj.
ClientExport.Serve(app, Path.Combine(AppContext.BaseDirectory, "wwwroot"));

app.MapAgentFactoryApi();

app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

app.Run();
