using SysCmd.Simulator;

// Standalone entry point. The server can also host this in-process with --simulate.
await using var host = new SimulatorHost();
host.Start();

Console.WriteLine("Press Ctrl+C to stop the simulator.");

var stopping = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.TrySetResult(); };
await stopping.Task;
