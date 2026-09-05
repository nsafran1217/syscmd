using Microsoft.Extensions.DependencyInjection;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Jobs;
using SysCmd.Core.Machines;
using SysCmd.Core.Mp;
using SysCmd.Core.Pdu;
using SysCmd.Core.Power;
using SysCmd.Core.Theming;

namespace SysCmd.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the whole control layer. Everything is a singleton: this is one process managing
    /// one lab, and the PDU cache, job queue and endpoint leases all need to be shared.
    /// </summary>
    public static IServiceCollection AddSysCmdCore(this IServiceCollection services, string configRoot, string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);

        services.AddSingleton(new ConfigPaths(configRoot));
        services.AddSingleton<ConfigStore>();

        services.AddSingleton(sp => new EventLog(dataRoot,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EventLog>>()));

        services.AddSingleton<SnmpPduClient>();
        services.AddSingleton<PduService>();

        services.AddSingleton<EndpointBroker>();
        services.AddSingleton<IMpDriver, ExpectScriptDriver>();

        services.AddSingleton<JobQueue>();
        services.AddSingleton<MachineLocks>();
        services.AddHostedService<JobRunner>();

        services.AddSingleton(sp => new PowerHistoryStore(dataRoot,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PowerHistoryStore>>()));
        services.AddSingleton<PowerSummaryCache>();
        services.AddHostedService<PowerPoller>();

        services.AddSingleton<MachineService>();
        services.AddSingleton<MachinePowerService>();
        services.AddSingleton<GroupService>();
        services.AddSingleton<LabStatusService>();

        return services;
    }

    /// <summary>
    /// Register the CDE palettes and backdrops. Kept separate from AddSysCmdCore because where the
    /// shipped assets live is a hosting question - the lab's own additions sit beside its YAML,
    /// and win over anything of the same name.
    /// </summary>
    public static IServiceCollection AddSysCmdTheming(
        this IServiceCollection services, string assetRoot, string configRoot)
    {
        string[] palettes = [Path.Combine(assetRoot, "palettes"), Path.Combine(configRoot, "palettes")];
        string[] backdrops = [Path.Combine(assetRoot, "backdrops"), Path.Combine(configRoot, "backdrops")];

        services.AddSingleton(sp => new PaletteStore(palettes,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PaletteStore>>()));
        services.AddSingleton(sp => new BackdropStore(backdrops,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackdropStore>>()));

        return services;
    }
}
