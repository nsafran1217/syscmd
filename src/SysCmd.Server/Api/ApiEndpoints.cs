using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Jobs;
using SysCmd.Core.Machines;
using SysCmd.Core.Mp;
using SysCmd.Core.Pdu;
using SysCmd.Core.Power;

namespace SysCmd.Server.Api;

/// <summary>
/// The REST control layer. The Blazor UI calls the same services directly in-process, so this
/// exists for everything else: a CLI, a future TUI, scripts, monitoring.
/// </summary>
public static class ApiEndpoints
{
    public static void MapSysCmdApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireLabAccess();

        MapStatus(api);
        MapMachines(api);
        MapPdus(api);
        MapGroups(api);
        MapJobs(api);
        MapPower(api);
        MapEvents(api);
        MapConfig(api);
    }

    private static void MapStatus(RouteGroupBuilder api) =>
        api.MapGet("/status", async (LabStatusService status, CancellationToken ct) =>
                Results.Ok(await status.GetAsync(ct)))
            .WithSummary("Lab overview: machines on, outlets on, current draw, energy and cost.");

    private static void MapMachines(RouteGroupBuilder api)
    {
        var machines = api.MapGroup("/machines");

        machines.MapGet("/", async (MachineService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        machines.MapGet("/{id}", async (string id, MachineService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } m ? Results.Ok(m) : Results.NotFound());

        machines.MapPost("/{id}/power", (string id, PowerRequest req, MachinePowerService power) =>
        {
            if (!req.TryGetAction(out var action))
                return Results.BadRequest(new { error = $"Unknown action '{req.Action}'. Use on, off or reset." });

            try
            {
                var job = power.EnqueueMachinePower(id, action, req.Force);
                return Results.Accepted($"/api/v1/jobs/{job.Id}", job.ToAccepted());
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithSummary("Power a machine on, off or reset. Set force to bypass the management processor.");
    }

    private static void MapPdus(RouteGroupBuilder api)
    {
        var pdus = api.MapGroup("/pdus");

        pdus.MapGet("/", async (PduService svc, CancellationToken ct) =>
            Results.Ok(await svc.ReadAllAsync(ct)));

        pdus.MapGet("/{id}", async (string id, PduService svc, CancellationToken ct) =>
            await svc.ReadAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

        pdus.MapGet("/{id}/outlets", async (string id, PduService svc, CancellationToken ct) =>
            await svc.ReadAsync(id, ct) is { } p ? Results.Ok(p.Outlets) : Results.NotFound());

        pdus.MapPost("/{id}/outlets/{outlet:int}", (
            string id, int outlet, PowerRequest req, MachinePowerService power) =>
        {
            if (!req.TryGetAction(out var action))
                return Results.BadRequest(new { error = $"Unknown action '{req.Action}'. Use on, off or reboot." });

            try
            {
                var job = power.EnqueueOutlet(id, outlet, action, req.Force);
                return Results.Accepted($"/api/v1/jobs/{job.Id}", job.ToAccepted());
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithSummary("Switch an outlet. A machine with an MP is powered safely unless force is set.");
    }

    private static void MapGroups(RouteGroupBuilder api)
    {
        var groups = api.MapGroup("/groups");

        groups.MapGet("/", async (GroupService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        groups.MapPost("/{id}/power", (string id, PowerRequest req, MachinePowerService power) =>
        {
            if (!req.TryGetAction(out var action) || action is not (PowerAction.On or PowerAction.Off))
                return Results.BadRequest(new { error = "Group actions must be on or off." });

            try
            {
                var job = power.EnqueueGroupPower(id, action, req.Force);
                return Results.Accepted($"/api/v1/jobs/{job.Id}", job.ToAccepted());
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static void MapJobs(RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs");

        jobs.MapGet("/", (JobQueue queue, int? limit) =>
            Results.Ok(queue.All(limit ?? 100).Select(j => j.ToDto())));

        jobs.MapGet("/{id}", (string id, JobQueue queue) =>
            queue.Get(id) is { } job ? Results.Ok(job.ToDto()) : Results.NotFound());

        jobs.MapGet("/active", (JobQueue queue) =>
            Results.Ok(queue.Active().Select(j => j.ToDto())));

        jobs.MapPost("/{id}/cancel", (string id, JobQueue queue) =>
        {
            if (queue.Get(id) is null) return Results.NotFound();
            return queue.Cancel(id)
                ? Results.Ok(new { stopped = true })
                : Results.BadRequest(new { error = "That job has already finished." });
        }).WithSummary("Stop a running or queued job. Outlets it had not yet switched are left alone.");
    }

    private static void MapPower(RouteGroupBuilder api)
    {
        var power = api.MapGroup("/power");

        power.MapGet("/summary", (PowerSummaryCache cache) => Results.Ok(cache.Current()));

        power.MapGet("/history", (
            PowerHistoryStore history, DateTimeOffset? from, DateTimeOffset? to, string? pdu, int? maxPoints) =>
        {
            var end = to ?? DateTimeOffset.Now;
            var start = from ?? end.AddHours(-24);
            var samples = history.Read(start, end, pdu);

            // Thin the series so a month of 15-second samples does not land on a chart untouched.
            var cap = Math.Clamp(maxPoints ?? 500, 10, 5000);
            var step = Math.Max(1, samples.Count / cap);

            return Results.Ok(samples
                .Where((_, i) => i % step == 0)
                .Select(s => new PowerPointDto(s.Timestamp, s.PduId, s.Watts)));
        });
    }

    private static void MapEvents(RouteGroupBuilder api) =>
        api.MapGet("/events", (EventLog log, int? limit, string? level, string? machine) =>
        {
            EventLevel? min = Enum.TryParse<EventLevel>(level, ignoreCase: true, out var parsed) ? parsed : null;
            return Results.Ok(log.Recent(limit ?? 200, min, machine).Select(e => e.ToDto()));
        });

    private static void MapConfig(RouteGroupBuilder api)
    {
        var config = api.MapGroup("/config");

        config.MapGet("/", (ConfigStore store) => Results.Ok(new
        {
            store.Current.App,
            store.Current.Issues,
            store.Current.LoadedAt,
            Root = store.Paths.Root,
        }));

        config.MapGet("/app", (ConfigStore store) => Results.Ok(store.Current.App));
        config.MapPut("/app", (AppConfig app, ConfigStore store) => Save(store, () => store.SaveApp(app)));

        config.MapGet("/machines", (ConfigStore store) => Results.Ok(store.Current.Machines.Values));
        config.MapGet("/machines/{id}", (string id, ConfigStore store) =>
            store.Current.Machine(id) is { } m ? Results.Ok(m) : Results.NotFound());
        config.MapPut("/machines/{id}", (string id, MachineConfig machine, ConfigStore store) =>
        {
            machine.Id = id;
            return Save(store, () => store.SaveMachine(machine));
        });
        config.MapDelete("/machines/{id}", (string id, ConfigStore store) =>
            Save(store, () => store.DeleteMachine(id)));

        config.MapGet("/pdus", (ConfigStore store) => Results.Ok(store.Current.Pdus.Values));
        config.MapPut("/pdus/{id}", (string id, PduConfig pdu, ConfigStore store) =>
        {
            pdu.Id = id;
            return Save(store, () => store.SavePdu(pdu));
        });
        config.MapDelete("/pdus/{id}", (string id, ConfigStore store) =>
            Save(store, () => store.DeletePdu(id)));

        config.MapGet("/console-servers", (ConfigStore store) => Results.Ok(store.Current.ConsoleServers.Values));
        config.MapPut("/console-servers/{id}", (string id, ConsoleServerConfig cs, ConfigStore store) =>
        {
            cs.Id = id;
            return Save(store, () => store.SaveConsoleServer(cs));
        });
        config.MapDelete("/console-servers/{id}", (string id, ConfigStore store) =>
            Save(store, () => store.DeleteConsoleServer(id)));

        config.MapGet("/groups", (ConfigStore store) => Results.Ok(store.Current.Groups));
        config.MapPut("/groups", (List<GroupConfig> groups, ConfigStore store) =>
            Save(store, () => store.SaveGroups(groups)));

        // Driver definitions are read-only over the API: they are edited as files or through the
        // GUI's raw YAML view, where their comments survive.
        config.MapGet("/types/pdu", (ConfigStore store) => Results.Ok(store.Current.PduTypes.Values));
        config.MapGet("/types/mp", (ConfigStore store) => Results.Ok(store.Current.MpTypes.Values));

        static IResult Save(ConfigStore store, Action save)
        {
            try
            {
                save();
                var errors = store.Current.Issues.Where(i => i.Severity == ConfigIssueSeverity.Error).ToList();
                return errors.Count > 0
                    ? Results.Ok(new { saved = true, warnings = errors })
                    : Results.Ok(new { saved = true });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }
    }
}
