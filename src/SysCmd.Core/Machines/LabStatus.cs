using SysCmd.Core.Configuration;
using SysCmd.Core.Jobs;
using SysCmd.Core.Pdu;
using SysCmd.Core.Power;

namespace SysCmd.Core.Machines;

/// <summary>The figures across the top of the dashboard.</summary>
public sealed record LabStatus(
    string SiteName,
    int MachinesOn,
    int MachinesTotal,
    int MachinesUnknown,
    int OutletsOn,
    int OutletsTotal,
    int PdusReachable,
    int PdusTotal,
    int ActiveJobs,
    PowerSummary Power)
{
    public int ConfigErrors { get; init; }
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;
}

public sealed class LabStatusService(
    ConfigStore config, PduService pdus, MachineService machines, PowerSummaryCache power, JobQueue jobs)
{
    public async Task<LabStatus> GetAsync(CancellationToken ct)
    {
        var snapshot = config.Current;
        var pduStates = await pdus.ReadAllAsync(ct);
        var machineStates = await machines.ListAsync(ct);

        return new LabStatus(
            SiteName: snapshot.App.Site.Name,
            MachinesOn: machineStates.Count(m => m.OutletState == PowerState.On),
            MachinesTotal: machineStates.Count,
            MachinesUnknown: machineStates.Count(m => m.OutletState == PowerState.Unknown),
            OutletsOn: pduStates.Sum(p => p.OnCount),
            OutletsTotal: pduStates.Sum(p => p.Outlets.Count),
            PdusReachable: pduStates.Count(p => p.Reachable),
            PdusTotal: pduStates.Count,
            ActiveJobs: jobs.Active().Count,
            Power: power.Current())
        {
            ConfigErrors = snapshot.Issues.Count(i => i.Severity == ConfigIssueSeverity.Error),
        };
    }
}
