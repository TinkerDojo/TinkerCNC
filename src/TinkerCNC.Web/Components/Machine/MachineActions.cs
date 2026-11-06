using TinkerCNC.Host;
namespace TinkerCNC.Web.Components.Machine;

/// <summary>A one-click machine command shown as a tile on the dashboard.</summary>
public sealed record MachineAction(string Label, string Icon, string Title, Func<MachineSession, Task> Execute, string Css = "");

public static class MachineActions
{
    public static readonly IReadOnlyList<MachineAction> All =
    [
        new("Hold", "pause", "Feed hold (!)", s => s.FeedHoldAsync()),
        new("Resume", "play", "Cycle start / resume (~)", s => s.CycleStartAsync()),
        new("Home", "home", "Home all axes ($H)", s => s.HomeAsync()),
        new("Unlock", "unlock", "Clear alarm ($X)", s => s.UnlockAsync()),
        new("Reset", "reset", "Soft reset (Ctrl-X)", s => s.ResetAsync(), "btn-danger"),
    ];
}
