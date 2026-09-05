namespace SysCmd.Simulator;

/// <summary>
/// Boots the whole fake lab: one SNMP PDU, a set of management processors, and the console-server
/// ports that reach them. The layout here matches the machines defined in config.sim/.
/// </summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _devices = [];

    public const int SnmpPort = 16100;

    public SimLab Lab { get; }

    public SimulatorHost()
    {
        Lab = new SimLab(outletCount: 8,
        [
            new SimMachine
            {
                Id = "rp3440", Name = "HP rp3440", Outlet = 1, MpPort = 2301, SerialPort = 2401,
                RunningWatts = 310, MpBootTime = TimeSpan.FromSeconds(8), ShutdownTime = TimeSpan.FromSeconds(20),
            },
            new SimMachine
            {
                Id = "rx2660", Name = "HP rx2660", Outlet = 2, MpPort = 2302, SerialPort = 2402,
                RunningWatts = 420, MpBootTime = TimeSpan.FromSeconds(10), ShutdownTime = TimeSpan.FromSeconds(25),
            },
            new SimMachine
            {
                Id = "ultra10", Name = "Sun Ultra 10", Outlet = 3, MpPort = 2303, SerialPort = null,
                RunningWatts = 145, MpBootTime = TimeSpan.FromSeconds(6), ShutdownTime = TimeSpan.FromSeconds(15),
            },
            // Deliberately refuses to shut down, so the confirm-then-cut timeout can be exercised.
            new SimMachine
            {
                Id = "alpha1000", Name = "AlphaServer 1000", Outlet = 4, MpPort = 2304, SerialPort = 2404,
                RunningWatts = 260, MpBootTime = TimeSpan.FromSeconds(7),
                Shutdown = ShutdownBehaviour.Stubborn,
            },
        ]);
    }

    public void Start()
    {
        var snmp = new SimSnmpAgent(Lab, SnmpPort);
        try
        {
            snmp.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new InvalidOperationException(
                $"The simulator could not bind 127.0.0.1:{SnmpPort} ({ex.Message}). " +
                "Another copy of syscmd is probably already running in simulate mode.", ex);
        }
        _devices.Add(snmp);

        foreach (var machine in Lab.Machines)
        {
            // The Sun box speaks ALOM; the rest are HP MPs.
            SimTelnetServer mp = machine.Id == "ultra10"
                ? new SimAlom(Lab, machine)
                : new SimHpMp(Lab, machine);
            mp.Start();
            _devices.Add(mp);

            if (machine.SerialPort is { } serialPort)
            {
                var console = new SimSerialConsole(Lab, machine, serialPort);
                console.Start();
                _devices.Add(console);
            }
        }

        // Start with outlet 1 live so there is something to look at immediately.
        Lab.SetOutlet(1, true);

        Console.WriteLine("[sim] fake lab is up");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices) await device.DisposeAsync();
        _devices.Clear();
    }
}
