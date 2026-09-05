namespace SysCmd.Server.Components.Shared;

/// <summary>
/// One entry in a window's pull-down menu. These are real actions, not decoration: the menu bar
/// in the reference screenshots was painted into the window image, but the frames are drawn in
/// CSS now, so the menus can do something.
/// </summary>
public sealed class CdeMenuItem
{
    public required string Label { get; init; }

    /// <summary>What the entry does. Null with <see cref="IsSeparator"/> set draws a divider.</summary>
    public Func<Task>? Action { get; init; }

    /// <summary>Shown right-aligned, e.g. a keyboard shortcut. Purely informational.</summary>
    public string? Accelerator { get; init; }

    public bool Disabled { get; init; }
    public bool IsSeparator { get; init; }

    /// <summary>
    /// Set to make the entry a toggle, drawn with Motif's square indicator to the left of the
    /// label. Null leaves it an ordinary command entry with no indicator.
    /// </summary>
    public bool? Checked { get; init; }

    /// <summary>Index of the mnemonic letter in <see cref="Label"/>, underlined the way Motif does.</summary>
    public int MnemonicIndex { get; init; } = -1;

    public static CdeMenuItem Separator { get; } = new() { Label = "", IsSeparator = true };

    public static CdeMenuItem Of(string label, Func<Task> action, string? accelerator = null, bool disabled = false)
        => new() { Label = label, Action = action, Accelerator = accelerator, Disabled = disabled };

    /// <summary>A toggle entry: same as <see cref="Of(string, Func{Task}, string?, bool)"/>, with an indicator.</summary>
    public static CdeMenuItem Toggle(string label, bool value, Func<Task> action, bool disabled = false)
        => new() { Label = label, Action = action, Checked = value, Disabled = disabled };

    /// <summary>Convenience for actions that do not need to await anything.</summary>
    public static CdeMenuItem Of(string label, Action action, string? accelerator = null, bool disabled = false)
        => new()
        {
            Label = label,
            Action = () => { action(); return Task.CompletedTask; },
            Accelerator = accelerator,
            Disabled = disabled,
        };
}

/// <summary>A single pull-down on a window's menu bar.</summary>
public sealed class CdeMenu
{
    public required string Label { get; init; }

    public IReadOnlyList<CdeMenuItem> Items { get; init; } = [];

    /// <summary>
    /// When set, the entry acts on click instead of opening a pull-down. Motif proper has no such
    /// thing, but a single one-shot action reads better on the bar than a menu holding one item.
    /// </summary>
    public Func<Task>? Action { get; init; }

    /// <summary>Index of the underlined letter in <see cref="Label"/>. Defaults to the first.</summary>
    public int MnemonicIndex { get; init; }

    public static CdeMenu Of(string label, params CdeMenuItem[] items)
        => new() { Label = label, Items = items };
}
