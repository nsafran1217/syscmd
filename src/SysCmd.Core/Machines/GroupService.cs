using SysCmd.Core.Configuration;

namespace SysCmd.Core.Machines;

/// <summary>A group as shown on the dashboard: how many of its members are currently up.</summary>
public sealed record GroupStatus(string Id, string Name, int MachineCount, int OnCount)
{
    public IReadOnlyList<string> Machines { get; init; } = [];
    public int StaggerSeconds { get; init; }
    public bool AllOn => MachineCount > 0 && OnCount == MachineCount;
    public bool AllOff => OnCount == 0;
    public bool IsBusy { get; init; }
}

public sealed class GroupService(ConfigStore config, MachineService machines)
{
    public async Task<IReadOnlyList<GroupStatus>> ListAsync(CancellationToken ct)
    {
        var snapshot = config.Current;
        var all = await machines.ListAsync(ct);
        var byId = all.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        return [.. snapshot.Groups.Select(g =>
        {
            var members = g.Machines.Select(byId.GetValueOrDefault).OfType<MachineStatus>().ToList();
            return new GroupStatus(
                g.Id,
                string.IsNullOrWhiteSpace(g.Name) ? g.Id : g.Name,
                members.Count,
                members.Count(m => m.OutletState == PowerState.On))
            {
                Machines = g.Machines,
                StaggerSeconds = g.StaggerSeconds,
                IsBusy = members.Any(m => m.IsBusy),
            };
        })];
    }
}
