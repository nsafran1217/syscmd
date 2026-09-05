using System.Text;
using SysCmd.Core.Configuration;
using SysCmd.Core.Mp;

namespace SysCmd.Server.Console;

/// <summary>
/// Replays an mp-type's login steps into a console session the browser already has open.
///
/// It does not read the socket itself: the bridge is already pumping every byte from the device
/// to the browser, so this watches that same stream and injects the next reply when the pattern
/// it is waiting for goes past. That keeps one reader on the session, and the operator sees the
/// whole exchange happen in their terminal.
/// </summary>
public sealed class LoginAssistant
{
    /// <summary>Give up if the expected prompt never arrives; a wedged MP should not hang here.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<ExpectStep> _steps;
    private readonly ScriptVariables _vars;
    private readonly StringBuilder _window = new();

    private int _index;
    private DateTimeOffset _deadline;

    private LoginAssistant(IReadOnlyList<ExpectStep> steps, ScriptVariables vars, string alreadySeen)
    {
        _steps = steps;
        _vars = vars;
        // The banner and prompt may have arrived before the operator pressed Login, so start
        // from what is already on screen rather than waiting for the MP to repeat itself.
        _window.Append(alreadySeen);
        _deadline = DateTimeOffset.Now + StepTimeout;
    }

    public bool Finished => _index >= _steps.Count;

    /// <summary>Why it stopped, when it stopped early.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Build one for a machine, or explain why the machine cannot be logged into this way.
    /// </summary>
    public static LoginAssistant? TryCreate(
        ConfigSnapshot snapshot, MachineConfig machine, string alreadySeen, out string error)
    {
        error = "";

        if (machine.Mp is not { } mp)
        {
            error = $"{machine.Name} has no management processor configured.";
            return null;
        }

        if (snapshot.MpTypes.GetValueOrDefault(mp.Type) is not { } type)
        {
            error = $"MP type '{mp.Type}' is not defined.";
            return null;
        }

        if (type.Login.Count == 0)
        {
            error = $"{type.Name} defines no login steps, so there is nothing to send.";
            return null;
        }

        return new LoginAssistant(type.Login, new ScriptVariables(mp.Username, mp.Password), alreadySeen);
    }

    /// <summary>
    /// Feed the bytes just sent to the browser. Returns what should be transmitted to the device,
    /// or null when it is still waiting. Call repeatedly: one chunk can satisfy several steps.
    /// </summary>
    public string? Observe(string fromDevice)
    {
        if (Finished) return null;

        _window.Append(fromDevice);

        var send = new StringBuilder();
        while (!Finished)
        {
            var step = _steps[_index];

            if (!string.IsNullOrEmpty(step.Expect))
            {
                if (!Matcher.Compile(step.Expect).IsMatch(_window.ToString()))
                {
                    if (DateTimeOffset.Now > _deadline)
                    {
                        Error = $"gave up waiting for \"{step.Expect}\"";
                        _index = _steps.Count;
                    }
                    break;
                }

                _window.Clear();
                _deadline = DateTimeOffset.Now + StepTimeout;
            }

            if (step.Send is not null)
            {
                send.Append(Escapes.Expand(step.Send, _vars));
                if (!step.NoNewline) send.Append('\r');
            }

            _index++;
        }

        // Keep the match window bounded; a console can be noisy.
        if (_window.Length > 8192) _window.Remove(0, _window.Length - 4096);

        return send.Length > 0 ? send.ToString() : null;
    }
}
