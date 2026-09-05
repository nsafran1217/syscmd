using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysCmd.Core.Configuration;
using SysCmd.Core.Pdu;

namespace SysCmd.Core.Power;

/// <summary>
/// Samples every PDU on a timer and writes the readings to history. This is the only thing that
/// writes power CSV, so the file stays append-ordered without any locking beyond the store's own.
/// </summary>
public sealed class PowerPoller(
    ConfigStore config,
    PduService pdus,
    PowerHistoryStore history,
    PowerSummaryCache summary,
    ILogger<PowerPoller> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first SNMP round trip.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(config.Current.App.Power.PollIntervalSeconds, 5));

            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Power poll failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var statuses = await pdus.ReadAllAsync(ct, fresh: true);
        var now = DateTimeOffset.Now;

        var samples = statuses
            .Where(s => s.Reachable && s.Watts is not null)
            .Select(s => new PowerSample(now, s.PduId, s.Watts!.Value, s.Amps ?? 0, s.Volts ?? 0))
            .ToList();

        if (samples.Count > 0)
        {
            history.Append(samples);
            summary.Record(samples);
        }

        foreach (var unreachable in statuses.Where(s => !s.Reachable))
            log.LogDebug("PDU {Pdu} unreachable: {Error}", unreachable.PduId, unreachable.Error);
    }
}
