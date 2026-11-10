using TinkerCNC.Host;
using TinkerCNC.Localization;
using TinkerCNC.Web.Localization;

namespace TinkerCNC.Web.Components.Machine;

/// <summary>A one-click machine command shown as a tile on the dashboard.</summary>
public sealed record MachineAction(Text Label, string Icon, Text Title, Func<MachineSession, Task> Execute, string Css = "");

public static class MachineActions
{
    public static readonly IReadOnlyList<MachineAction> All =
    [
        new(Texts.Of("Machine.Control.Hold"), "pause", Texts.Of("Machine.Control.HoldTitle"), s => s.FeedHoldAsync()),
        new(Texts.Of("Common.Resume"), "play", Texts.Of("Machine.Control.ResumeTitle"), s => s.CycleStartAsync()),
        new(Texts.Of("Machine.Control.Home"), "home", Texts.Of("Machine.Control.HomeTitle"), s => s.HomeAsync()),
        new(Texts.Of("Machine.Control.Unlock"), "unlock", Texts.Of("Machine.Control.UnlockTitle"), s => s.UnlockAsync()),
        new(Texts.Of("Machine.Control.Reset"), "reset", Texts.Of("Machine.Control.ResetTitle"), s => s.ResetAsync(), "btn-danger"),
    ];
}
