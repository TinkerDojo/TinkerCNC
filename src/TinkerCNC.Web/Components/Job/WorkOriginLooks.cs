using TinkerCNC.Jobs;

namespace TinkerCNC.Web.Components.Job;

/// <summary>How a work origin choice is called and explained in the setup, and which fields it needs.</summary>
/// <param name="Offset">Whether it takes an X/Y offset.</param>
/// <param name="ProbeBlock">Whether it takes the probe block's height and the tool diameter.</param>
public sealed record WorkOriginLook(string Label, string Hint, bool Offset = false, bool ProbeBlock = false);

public static class WorkOriginLooks
{
    public static readonly IReadOnlyDictionary<WorkOriginChoice, WorkOriginLook> All = new Dictionary<WorkOriginChoice, WorkOriginLook>
    {
        [WorkOriginChoice.Keep] = new("Keep", "The work origin stays where it is."),
        [WorkOriginChoice.BedCorner] = new("Bed corner",
            "The bed's reference corner plus the offset becomes X/Y zero when the job starts.", Offset: true),
        [WorkOriginChoice.HeadPosition] = new("Head position",
            "Where the head is now, plus the offset, becomes X/Y zero when the job starts.", Offset: true),
        [WorkOriginChoice.XyzProbe] = new("XYZ probe",
            "Put the probe block on the stock's front left corner and jog the tool above it: the machine touches it " +
            "from above, the right and the front and makes the stock's corner and top zero.", ProbeBlock: true),
    };

    public static WorkOriginLook For(WorkOriginChoice choice) => All[choice];
}
