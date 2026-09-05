using System.Text.Json.Serialization;
using SysCmd.Core;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Mp;
using SysCmd.Core.Power;
using SysCmd.Server.Api;
using SysCmd.Server.Components;
using SysCmd.Server.Console;
using SysCmd.Server.Theming;
using SysCmd.Simulator;

var builder = WebApplication.CreateBuilder(args);

// --simulate boots the fake lab in-process and points at config.sim/, so the whole app can be
// exercised with no PDU or management processor on the network.
var simulate = args.Contains("--simulate") || builder.Configuration.GetValue<bool>("Simulate");

var repoRoot = FindRepoRoot(builder.Environment.ContentRootPath);
var configRoot = builder.Configuration["ConfigRoot"]
    ?? Path.Combine(repoRoot, simulate ? "config.sim" : "config");
var dataRoot = builder.Configuration["DataRoot"]
    ?? Path.Combine(repoRoot, simulate ? "data.sim" : "data");

// A first run against real hardware with no config at all is a confusing empty dashboard,
// so point at the templates rather than silently creating empty directories.
if (!simulate && !Directory.Exists(configRoot))
{
    Console.WriteLine($"No configuration found at {configRoot}.");
    Console.WriteLine($"Copy the documented templates to start:  cp -r {Path.Combine(repoRoot, "config.example")} {configRoot}");
    Console.WriteLine("Or run against the built-in fake lab:      dotnet run --project src/SysCmd.Server -- --simulate");
}

builder.Services.AddSysCmdCore(configRoot, dataRoot);

// CDE palettes and backdrops ship in wwwroot; the lab may add its own beside its YAML.
builder.Services.AddSysCmdTheming(
    Path.Combine(builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"), "cde"),
    configRoot);
builder.Services.AddSingleton<ThemeResolver>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<ConsoleBridge>();

// Scoped to the browser circuit: the console windows one person has open are theirs.
builder.Services.AddScoped<ConsoleWindowManager>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Enum values travel as names, so a CLI client sees "On" rather than 1.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

if (simulate) builder.Services.AddSingleton<SimulatorHost>();

var app = builder.Build();

if (simulate) app.Services.GetRequiredService<SimulatorHost>().Start();

// Load config before anything serves a request, then keep watching for hand edits.
var store = app.Services.GetRequiredService<ConfigStore>();
store.Load();
store.StartWatching();

app.Services.GetRequiredService<PowerSummaryCache>().Seed();

var events = app.Services.GetRequiredService<EventLog>();
events.Info("app", $"syscmd started ({(simulate ? "simulated" : "live")} hardware), config at {configRoot}");

foreach (var issue in store.Current.Issues)
    events.Write(issue.Severity == ConfigIssueSeverity.Error ? EventLevel.Error : EventLevel.Warning,
        "config", $"{issue.File}: {issue.Message}");

app.UseStaticFiles();
app.UseAntiforgery();
// A console is expected to sit idle - that is what a console is for - but an idle WebSocket is
// exactly what a reverse proxy reaps. ASP.NET's default keep-alive is two minutes, which is
// longer than the usual proxy idle timeout, so the ping that would have saved the connection
// arrives after it has already been cut. Thirty seconds keeps a silent console alive behind any
// proxy idle timeout of a minute or more, without asking anyone to configure theirs.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapSysCmdApi();
app.MapSysCmdTheme();

// The browser terminal talks to a real telnet session through here.
app.Map("/ws/console/{machineId}", async (
    HttpContext context, string machineId, string? target, ConsoleBridge bridge) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("This endpoint expects a WebSocket connection.");
        return;
    }

    var which = string.Equals(target, "serial", StringComparison.OrdinalIgnoreCase)
        ? ConsoleTarget.Serial
        : ConsoleTarget.Mp;

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await bridge.HandleAsync(socket, machineId, which, context.RequestAborted);
}).RequireLabAccess();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Walk up from the content root to find the directory holding the config folders, so the app can
/// be started from the repo root or from inside bin/ without extra configuration.
/// </summary>
static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "SysCmd.slnx")) ||
            Directory.Exists(Path.Combine(dir.FullName, "config.sim")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return start;
}
