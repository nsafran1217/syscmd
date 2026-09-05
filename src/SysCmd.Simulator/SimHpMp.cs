namespace SysCmd.Simulator;

/// <summary>
/// Emulates the HP Integrity MP command tree: log in, "cm" for command mode, "pc" for power
/// control, "ps" for power status. The prompts match what the shipped hp-mp.yaml expects.
/// </summary>
public sealed class SimHpMp(SimLab lab, SimMachine machine)
    : SimTelnetServer($"{machine.Name} MP", machine.MpPort!.Value)
{
    protected override bool IsPowered => lab.IsOutletOn(machine.Outlet) && machine.MpReachable;

    // A real HP Integrity MP serves several telnet logins at once.
    protected override bool AllowsConcurrentSessions => true;

    protected override async Task RunSessionAsync(SimTelnetConnection conn, CancellationToken ct)
    {
        await conn.SendAsync(
            $"\n\nHewlett-Packard Management Processor\n(c) Copyright 1999-2008 Hewlett-Packard Development Company, L.P.\n\n" +
            $"MP Host Name: {machine.Id}-mp\n\n", ct);

        await conn.SendAsync("login: ", ct);
        if (await conn.ReadLineAsync(ct) is null) return;
        await conn.SendAsync("\npassword: ", ct);
        if (await conn.ReadLineAsync(ct) is null) return;

        await conn.SendAsync("\n\n     (Use Ctrl-B to return to MP main menu.)\n\nMP> ", ct);

        while (!ct.IsCancellationRequested)
        {
            var line = await conn.ReadLineAsync(ct);
            if (line is null) return;

            switch (line.Trim().ToLowerInvariant())
            {
                case "cm":
                    await conn.SendAsync("\n\n                          (Use Ctrl-B to return to MP main menu.)\n\nCM:hpiLO-> ", ct);
                    await CommandModeAsync(conn, ct);
                    break;

                case "" :
                    await conn.SendAsync("\nMP> ", ct);
                    break;

                case "x" or "exit" or "quit":
                    await conn.SendAsync("\nConnection closed.\n", ct);
                    return;

                default:
                    await conn.SendAsync($"\nUnrecognized command: {line}\n\nMP> ", ct);
                    break;
            }
        }
    }

    /// <summary>The "CM:" sub-shell, where power actually gets switched.</summary>
    private async Task CommandModeAsync(SimTelnetConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await conn.ReadLineAsync(ct);
            if (line is null) return;

            switch (line.Trim().ToLowerInvariant())
            {
                case "pc":
                    await PowerControlAsync(conn, ct);
                    break;

                case "ps":
                    lab.Tick();
                    var state = machine.Power switch
                    {
                        SimPower.On => "On",
                        SimPower.ShuttingDown => "On",     // still drawing power until it finishes
                        _ => "Off",
                    };
                    await conn.SendAsync(
                        $"\n\nPS\n\nSystem Power state: {state}\n\nCM:hpiLO-> ", ct);
                    break;

                case "ma":
                    await conn.SendAsync("\n\nMP> ", ct);
                    return;

                case "":
                    await conn.SendAsync("\nCM:hpiLO-> ", ct);
                    break;

                default:
                    await conn.SendAsync($"\nUnrecognized command: {line}\n\nCM:hpiLO-> ", ct);
                    break;
            }
        }
    }

    private async Task PowerControlAsync(SimTelnetConnection conn, CancellationToken ct)
    {
        lab.Tick();
        var current = machine.Power == SimPower.Off ? "off" : "on";

        await conn.SendAsync(
            $"\n\nPC\n\nSystem is currently {current}.\n\n" +
            "    ON       - Turn the system power on\n" +
            "    OFF      - Turn the system power off\n" +
            "    G        - Graceful shutdown\n\n" +
            "Enter menu item or [Q] to Quit: ", ct);

        var choice = (await conn.ReadLineAsync(ct))?.Trim().ToLowerInvariant();
        if (choice is null) return;

        if (choice is "q" or "")
        {
            await conn.SendAsync("\n\nCM:hpiLO-> ", ct);
            return;
        }

        if (choice is not ("on" or "off" or "g"))
        {
            await conn.SendAsync("\nInvalid selection.\n\nCM:hpiLO-> ", ct);
            return;
        }

        await conn.SendAsync($"\n\nConfirm? (Y/[N]): ", ct);
        var confirm = (await conn.ReadLineAsync(ct))?.Trim().ToLowerInvariant();
        if (confirm is null) return;

        if (confirm is not ("y" or "yes"))
        {
            await conn.SendAsync("\n\nCommand aborted.\n\nCM:hpiLO-> ", ct);
            return;
        }

        switch (choice)
        {
            case "on":
                lab.PowerOn(machine);
                await conn.SendAsync("\n\n-> System will be powered on.\n\nCM:hpiLO-> ", ct);
                break;
            case "g":
                lab.BeginShutdown(machine);
                await conn.SendAsync("\n\n-> Graceful shutdown initiated.\n\nCM:hpiLO-> ", ct);
                break;
            case "off":
                lab.HardOff(machine);
                await conn.SendAsync("\n\n-> System will be powered off.\n\nCM:hpiLO-> ", ct);
                break;
        }
    }
}
