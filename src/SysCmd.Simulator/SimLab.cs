namespace SysCmd.Simulator;

/// <summary>How a simulated machine behaves when asked to shut down.</summary>
public enum ShutdownBehaviour
{
    /// <summary>Powers off after a realistic delay.</summary>
    Normal,
    /// <summary>Accepts the command and never actually powers off — exercises the confirm timeout.</summary>
    Stubborn,
}

public enum SimPower { Off, On, ShuttingDown }

/// <summary>One simulated machine hanging off an outlet.</summary>
public sealed class SimMachine
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Outlet { get; init; }

    /// <summary>TCP port its management processor listens on.</summary>
    public required int MpPort { get; init; }

    /// <summary>Optional console-server port that reaches its serial console.</summary>
    public int? SerialPort { get; init; }

    /// <summary>Watts drawn when running. Standby draw is a small fraction of this.</summary>
    public double RunningWatts { get; init; } = 200;

    /// <summary>How long after outlet power before the MP answers, like a real service processor booting.</summary>
    public TimeSpan MpBootTime { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long a graceful shutdown takes once accepted.</summary>
    public TimeSpan ShutdownTime { get; init; } = TimeSpan.FromSeconds(20);

    public ShutdownBehaviour Shutdown { get; init; } = ShutdownBehaviour.Normal;

    // ---- mutable simulation state ----
    public SimPower Power { get; set; } = SimPower.Off;
    public DateTimeOffset? MpReadyAt { get; set; }
    public DateTimeOffset? ShutdownCompletesAt { get; set; }

    public bool MpReachable => MpReadyAt is { } at && DateTimeOffset.Now >= at;

    /// <summary>Standby draw covers the service processor and fans, which run on outlet power alone.</summary>
    public double CurrentWatts => Power switch
    {
        SimPower.On => RunningWatts,
        SimPower.ShuttingDown => RunningWatts * 0.7,
        _ => MpReadyAt is not null ? RunningWatts * 0.08 : 0,
    };
}

/// <summary>
/// Shared state for the fake hardware: which outlets are live, and what each machine is doing.
/// The SNMP agent and the MP servers both read and mutate this, which is what makes the
/// simulation coherent — cutting an outlet really does take its management processor away.
/// </summary>
public sealed class SimLab
{
    private readonly Lock _lock = new();
    private readonly Random _random = new(1234);

    public SimLab(int outletCount, IEnumerable<SimMachine> machines)
    {
        OutletCount = outletCount;
        Outlets = new bool[outletCount + 1];      // 1-based to match SNMP outlet numbering
        OutletNames = new string[outletCount + 1];
        Machines = [.. machines];

        for (var i = 1; i <= outletCount; i++)
            OutletNames[i] = Machines.FirstOrDefault(m => m.Outlet == i)?.Name ?? $"Outlet {i}";
    }

    public int OutletCount { get; }
    public bool[] Outlets { get; }
    public string[] OutletNames { get; }
    public IReadOnlyList<SimMachine> Machines { get; }

    public SimMachine? MachineOnOutlet(int outlet) => Machines.FirstOrDefault(m => m.Outlet == outlet);
    public SimMachine? MachineOnMpPort(int port) => Machines.FirstOrDefault(m => m.MpPort == port);

    public bool IsOutletOn(int outlet)
    {
        lock (_lock) return outlet >= 1 && outlet <= OutletCount && Outlets[outlet];
    }

    public void SetOutlet(int outlet, bool on)
    {
        if (outlet < 1 || outlet > OutletCount) return;

        lock (_lock)
        {
            if (Outlets[outlet] == on) return;
            Outlets[outlet] = on;

            if (MachineOnOutlet(outlet) is not { } machine) return;

            if (on)
            {
                // Outlet power wakes the service processor; the system itself stays off.
                machine.MpReadyAt = DateTimeOffset.Now + machine.MpBootTime;
                machine.Power = SimPower.Off;
                machine.ShutdownCompletesAt = null;
                Console.WriteLine($"[sim] outlet {outlet} on — {machine.Name} MP booting ({machine.MpBootTime.TotalSeconds:F0}s)");
            }
            else
            {
                machine.MpReadyAt = null;
                machine.Power = SimPower.Off;
                machine.ShutdownCompletesAt = null;
                Console.WriteLine($"[sim] outlet {outlet} off — {machine.Name} dark");
            }
        }
    }

    /// <summary>Advance any in-progress shutdowns. Called by the SNMP agent and the MP servers.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            foreach (var m in Machines)
            {
                if (m.Power != SimPower.ShuttingDown) continue;
                if (m.Shutdown == ShutdownBehaviour.Stubborn) continue;
                if (m.ShutdownCompletesAt is { } at && DateTimeOffset.Now >= at)
                {
                    m.Power = SimPower.Off;
                    m.ShutdownCompletesAt = null;
                    Console.WriteLine($"[sim] {m.Name} finished shutting down");
                }
            }
        }
    }

    public void PowerOn(SimMachine machine)
    {
        lock (_lock)
        {
            if (!IsOutletOn(machine.Outlet)) return;
            machine.Power = SimPower.On;
            machine.ShutdownCompletesAt = null;
            Console.WriteLine($"[sim] {machine.Name} powered on");
        }
    }

    public void BeginShutdown(SimMachine machine)
    {
        lock (_lock)
        {
            if (machine.Power != SimPower.On) return;
            machine.Power = SimPower.ShuttingDown;
            machine.ShutdownCompletesAt = DateTimeOffset.Now + machine.ShutdownTime;
            Console.WriteLine(machine.Shutdown == ShutdownBehaviour.Stubborn
                ? $"[sim] {machine.Name} accepted power-off and will ignore it (stubborn)"
                : $"[sim] {machine.Name} shutting down ({machine.ShutdownTime.TotalSeconds:F0}s)");
        }
    }

    public void HardOff(SimMachine machine)
    {
        lock (_lock)
        {
            machine.Power = SimPower.Off;
            machine.ShutdownCompletesAt = null;
            Console.WriteLine($"[sim] {machine.Name} powered off hard");
        }
    }

    /// <summary>Total draw across the PDU, with a little jitter so the graphs are not flat lines.</summary>
    public double TotalWatts()
    {
        Tick();
        lock (_lock)
        {
            var sum = Machines.Where(m => IsOutletOn(m.Outlet)).Sum(m => m.CurrentWatts);

            // Outlets with no machine behind them still show a trickle, as in a real rack.
            for (var i = 1; i <= OutletCount; i++)
                if (Outlets[i] && MachineOnOutlet(i) is null) sum += 12;

            return sum <= 0 ? 0 : Math.Max(0, sum + (_random.NextDouble() - 0.5) * sum * 0.04);
        }
    }
}
