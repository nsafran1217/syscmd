using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Consoles;

/// <summary>
/// Applies the console-log retention setting: once shortly after startup, so a lab that was off for
/// a month does not wait an hour to catch up, and hourly after that. Saving the setting on the log
/// page prunes straight away as well.
/// </summary>
public sealed class ConsoleLogPruner(ConsoleLogStore logs, ILogger<ConsoleLogPruner> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                try { logs.Prune(); }
                catch (Exception ex) { log.LogError(ex, "Console log pruning failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
