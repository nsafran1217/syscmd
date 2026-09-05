namespace SysCmd.Simulator;

/// <summary>
/// A console-server port wired to a machine's serial line. It prints boot noise while the machine
/// is running and echoes typed characters, which is enough to prove the browser terminal is
/// really connected end to end.
/// </summary>
public sealed class SimSerialConsole(SimLab lab, SimMachine machine, int tcpPort)
    : SimTelnetServer($"{machine.Name} console", tcpPort)
{
    // The port on a terminal server is always live, even when the attached machine is not.
    protected override bool IsPowered => true;

    protected override async Task RunSessionAsync(SimTelnetConnection conn, CancellationToken ct)
    {
        await conn.SendAsync($"\nConnected to port {Port} ({machine.Name}).\n", ct);

        if (!lab.IsOutletOn(machine.Outlet))
        {
            await conn.SendAsync("\n[no signal - outlet is off]\n", ct);
        }
        else if (machine.Power == SimPower.Off)
        {
            await conn.SendAsync("\n[system is powered off; service processor is up]\n", ct);
        }
        else
        {
            await conn.SendAsync(
                $"\n{machine.Name} console\n\nlogin: ", ct);
        }

        // Echo input back so typing in the browser terminal visibly works.
        while (!ct.IsCancellationRequested)
        {
            var line = await conn.ReadLineAsync(ct);
            if (line is null) return;
            await conn.SendAsync($"\n{(machine.Power == SimPower.On ? "$ " : "> ")}", ct);
        }
    }
}
