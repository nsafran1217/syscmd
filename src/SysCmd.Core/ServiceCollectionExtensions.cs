using Microsoft.Extensions.DependencyInjection;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Jobs;
using SysCmd.Core.Machines;
using SysCmd.Core.Mp;
using SysCmd.Core.Pdu;
using SysCmd.Core.Power;

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
}
