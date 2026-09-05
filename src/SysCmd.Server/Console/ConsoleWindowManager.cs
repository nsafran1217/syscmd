using SysCmd.Core.Mp;

namespace SysCmd.Server.Console;

/// <summary>One open console window on the page.</summary>
public sealed class ConsoleWindow
{
    public required string Id { get; init; }
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public required ConsoleTarget Target { get; init; }

    public string Title => $"{MachineName} - {(Target == ConsoleTarget.Mp ? "management processor" : "serial console")}";

    /// <summary>The DOM id the terminal attaches to; also what the JS side keys its instance by.</summary>
    public string TerminalElementId => $"term-{Id}";

    public string WindowElementId => $"win-{Id}";

    // Geometry, in CSS pixels. The window manager cascades new windows so they do not stack up.
    //
    // The default is sized from the terminal rather than picked by eye: at the shipped 13px mono
    // stack a cell measures 8.07 x 15.04, so this lands around 90x29 - comfortably clear of the
    // 80x24 a terminal is entitled to, with room to lose a line to the notice bar. See
    // TerminalWindow's MinWidth/MinHeight for the floor.
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 760;
    public int Height { get; set; } = 520;

    public int ZIndex { get; set; }
    public bool Shaded { get; set; }
    public bool Maximised { get; set; }

    /// <summary>Set once the browser has attached a terminal, so we do not attach twice.</summary>
    public bool Attached { get; set; }

    /// <summary>
    /// Forces a plain black terminal instead of following the palette. Per window and not
    /// persisted, like the geometry beside it: it survives navigating away, not a reload.
    /// </summary>
    public bool BlackBackground { get; set; }
}

/// <summary>
/// Tracks the console windows open in one browser session. It lives above the router so a session
/// survives moving between pages - closing a console should be a deliberate act, not a side effect
/// of clicking Configuration.
/// </summary>
public sealed class ConsoleWindowManager
{
    private readonly List<ConsoleWindow> _windows = [];
    private int _nextZ = 1;
    private int _cascade;

    public IReadOnlyList<ConsoleWindow> Windows => _windows;

    /// <summary>Raised when a window is opened, closed or re-ordered, so the host can re-render.</summary>
    public event Action? Changed;

    public ConsoleWindow? Top => _windows.Count == 0 ? null : _windows.MaxBy(w => w.ZIndex);

    /// <summary>
    /// Open a console, or raise the existing one. Opening the same target twice would fight over
    /// the endpoint lease, so the second request just brings the first window forward.
    /// </summary>
    public ConsoleWindow Open(string machineId, string machineName, ConsoleTarget target)
    {
        if (_windows.FirstOrDefault(w => w.MachineId == machineId && w.Target == target) is { } existing)
        {
            Focus(existing.Id);
            return existing;
        }

        // Cascade down and right so a second window does not land exactly on the first.
        var step = _cascade++ % 6;

        var window = new ConsoleWindow
        {
            Id = Guid.NewGuid().ToString("n")[..8],
            MachineId = machineId,
            MachineName = machineName,
            Target = target,
            X = 60 + step * 28,
            Y = 60 + step * 26,
            ZIndex = _nextZ++,
        };

        _windows.Add(window);
        Changed?.Invoke();
        return window;
    }

    public void Close(string id)
    {
        if (_windows.RemoveAll(w => w.Id == id) > 0) Changed?.Invoke();
    }

    public void CloseAll()
    {
        if (_windows.Count == 0) return;
        _windows.Clear();
        Changed?.Invoke();
    }

    public void Focus(string id)
    {
        if (_windows.FirstOrDefault(w => w.Id == id) is not { } window) return;
        if (window.ZIndex == _nextZ - 1) return;      // already on top
        window.ZIndex = _nextZ++;
        Changed?.Invoke();
    }

    public void Notify() => Changed?.Invoke();
}
