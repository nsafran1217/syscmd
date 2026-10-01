using Microsoft.JSInterop;
using SysCmd.Core.Pdu;

namespace SysCmd.Server.Components.Shared;

/// <summary>
/// Which PDUs this browser leaves out of the power and cost figures. Remembered per browser, like
/// the dashboard's other view preferences, because it is a question of what one person wants to
/// look at rather than of what the lab is.
///
/// The PDUs left <em>out</em> are what is stored, not the ones counted, so a PDU added to the
/// config later counts from the start instead of silently going missing from the totals.
/// </summary>
public static class PowerExclusions
{
    private const string Key = "syscmd.powerExcludedPdus";

    public static async Task<HashSet<string>> ReadAsync(IJSRuntime js)
    {
        var stored = await js.InvokeAsync<string?>("syscmdUi.readSetting", Key);
        return new HashSet<string>(
            (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public static async Task WriteAsync(IJSRuntime js, IEnumerable<string> excluded) =>
        await js.InvokeVoidAsync("syscmdUi.writeSetting", Key, string.Join(",", excluded));

    /// <summary>The names of the excluded PDUs that exist, for saying what the figures leave out.</summary>
    public static IReadOnlyList<string> Names(IReadOnlySet<string> excluded, IEnumerable<PduStatus> pdus) =>
        [.. pdus.Where(p => excluded.Contains(p.PduId)).Select(p => p.Name)];
}
