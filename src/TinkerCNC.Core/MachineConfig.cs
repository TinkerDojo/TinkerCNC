namespace TinkerCNC;

/// <summary>
/// Reading single settings of the machine's configuration (<c>config.txt</c> on its SD card) with the console command
/// <c>config-get sd &lt;key&gt;</c>. The firmware answers <c>"sd: &lt;key&gt; is set to &lt;value&gt;"</c> or
/// <c>"sd: &lt;key&gt; is not in config"</c>; values written with underscores stand for spaces in G-code settings.
/// </summary>
public static class MachineConfig
{
    /// <summary>G-code the machine runs right after a program is suspended (it may move the head).</summary>
    public const string AfterSuspendGCode = "after_suspend_gcode";

    public static string GetCommand(string key) => $"config-get sd {key}";

    /// <summary>Whether <paramref name="reply"/> answers a <see cref="GetCommand"/> for <paramref name="key"/>.</summary>
    public static bool IsReplyFor(string reply, string key) =>
        reply.Contains($" {key} is set to ", StringComparison.Ordinal) || reply.Contains($" {key} is not in config", StringComparison.Ordinal);

    /// <summary>The value in a reply, or null when the key is not set.</summary>
    public static string? ValueIn(string reply, string key)
    {
        var marker = $" {key} is set to ";
        var at = reply.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : reply[(at + marker.Length)..].Trim() is { Length: > 0 } value ? value : null;
    }
}
