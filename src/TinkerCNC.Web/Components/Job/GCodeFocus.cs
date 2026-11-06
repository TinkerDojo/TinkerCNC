namespace TinkerCNC.Web.Components.Job;

/// <summary>
/// Which line the G-code panel shows: the line the machine executes (following) or one picked on the timeline.
/// Shared by the timeline (which picks) and the G-code panel (which shows) of one machine page.
/// </summary>
public sealed class GCodeFocus
{
    public bool Follow { get; private set; } = true;

    /// <summary>Line picked on the timeline; used while not following.</summary>
    public int? Line { get; private set; }

    /// <summary>A line was picked: the G-code panel opens on it.</summary>
    public event Action? LinePicked;

    /// <summary>Following was switched back on.</summary>
    public event Action? FollowResumed;

    public void Show(int line)
    {
        Line = line;
        Follow = false;
        LinePicked?.Invoke();
    }

    public void FollowMachine()
    {
        Follow = true;
        FollowResumed?.Invoke();
    }
}
