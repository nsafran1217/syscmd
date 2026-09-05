namespace SysCmd.Simulator;

/// <summary>
/// Emulates a Sun ALOM service processor: a flat "sc&gt;" prompt with poweron / poweroff / reset
/// and showplatform, which is a very different shape from the HP MP's nested menus — useful for
/// checking the expect engine is not accidentally specialised to one device.
/// </summary>
public sealed class SimAlom(SimLab lab, SimMachine machine)
    : SimTelnetServer($"{machine.Name} ALOM", machine.MpPort!.Value)
{
    protected override bool IsPowered => lab.IsOutletOn(machine.Outlet) && machine.MpReachable;

    protected override async Task RunSessionAsync(SimTelnetConnection conn, CancellationToken ct)
    {
        await conn.SendAsync("\n\nSun(tm) Advanced Lights Out Manager\n\n", ct);
        await conn.SendAsync("Please login: ", ct);
        if (await conn.ReadLineAsync(ct) is null) return;
        await conn.SendAsync("\nPlease Enter password: ", ct);
        if (await conn.ReadLineAsync(ct) is null) return;

        await conn.SendAsync("\n\nsc> ", ct);

        while (!ct.IsCancellationRequested)
        {
            var line = await conn.ReadLineAsync(ct);
            if (line is null) return;

            // ALOM takes flags like "poweroff -f -y" on the same line as the command.
            var words = line.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = words.FirstOrDefault() ?? "";
            var forced = words.Contains("-f");

            switch (command)
            {
                case "poweron":
                    lab.PowerOn(machine);
                    await conn.SendAsync("\n\nsc> ", ct);
                    break;

                case "poweroff":
                    if (forced) lab.HardOff(machine); else lab.BeginShutdown(machine);
                    await conn.SendAsync("\nSC Alert: Host system has shut down.\n\nsc> ", ct);
                    break;

                case "reset":
                    lab.PowerOn(machine);
                    await conn.SendAsync("\nSC Alert: Host System has Reset.\n\nsc> ", ct);
                    break;

                case "showplatform":
                    lab.Tick();
                    var state = machine.Power == SimPower.Off ? "Powered Off" : "Running";
                    await conn.SendAsync(
                        $"\nDomain Status\n------ ------\n{machine.Id}  {state}\n\nsc> ", ct);
                    break;

                case "":
                    await conn.SendAsync("\nsc> ", ct);
                    break;

                case "logout" or "exit":
                    await conn.SendAsync("\n", ct);
                    return;

                default:
                    await conn.SendAsync($"\nInvalid command: {command}\n\nsc> ", ct);
                    break;
            }
        }
    }
}
