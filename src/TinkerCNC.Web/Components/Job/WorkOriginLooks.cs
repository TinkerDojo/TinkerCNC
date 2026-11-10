using TinkerCNC.Jobs;
using TinkerCNC.Localization;
using TinkerCNC.Web.Localization;

namespace TinkerCNC.Web.Components.Job;

/// <summary>How a work origin choice is called and explained in the setup, and which fields it needs.</summary>
/// <param name="Offset">Whether it takes an X/Y offset.</param>
/// <param name="ProbeBlock">Whether it takes the probe block's height and the tool diameter.</param>
public sealed record WorkOriginLook(Text Label, Text Hint, bool Offset = false, bool ProbeBlock = false);

public static class WorkOriginLooks
{
    public static readonly IReadOnlyDictionary<WorkOriginChoice, WorkOriginLook> All = new Dictionary<WorkOriginChoice, WorkOriginLook>
    {
        [WorkOriginChoice.Keep] = new(Texts.Of("Job.Origin.Keep"), Texts.Of("Job.Origin.Keep.Hint")),
        [WorkOriginChoice.BedCorner] = new(Texts.Of("Job.Origin.BedCorner"), Texts.Of("Job.Origin.BedCorner.Hint"), Offset: true),
        [WorkOriginChoice.HeadPosition] = new(Texts.Of("Job.Origin.HeadPosition"), Texts.Of("Job.Origin.HeadPosition.Hint"), Offset: true),
        [WorkOriginChoice.XyzProbe] = new(Texts.Of("Job.Origin.XyzProbe"), Texts.Of("Job.Origin.XyzProbe.Hint"), ProbeBlock: true),
    };

    public static WorkOriginLook For(WorkOriginChoice choice) => All[choice];
}
