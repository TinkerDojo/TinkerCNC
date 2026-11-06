using TinkerCNC.Jobs;
using TinkerCNC.Motion;

namespace TinkerCNC;

/// <summary>A kind of machine, recognised by its travel (mm), and what differs between kinds.</summary>
/// <param name="Name">Short name shown to users.</param>
/// <param name="JobModes">Job options it offers (<see cref="Jobs.JobModes"/>).</param>
public sealed record MachineType(string Name, double TravelX, double TravelY, IAirOutput Air, IReadOnlyList<JobMode> JobModes)
{
    private const double TravelTolerance = 3;

    public bool Fits(MachineTravel travel) =>
        Math.Abs(travel.X.Length - TravelX) <= TravelTolerance && Math.Abs(travel.Y.Length - TravelY) <= TravelTolerance;
}

/// <summary>
/// The kinds of machine TinkerCNC tells apart. A machine not listed here gets the firmware's defaults: compressed air
/// (<c>M7</c>/<c>M9</c>) and the vacuum and external-output job modes.
/// </summary>
public static class MachineTypes
{
    /// <summary>
    /// The compact desktop machine with a 207 × 206 mm travel: it blows chips away with its spindle blower instead of
    /// compressed air and offers the blow, bed-clean, anti-static, external-output and time-lapse job modes.
    /// </summary>
    public static readonly MachineType Compact = new("compact", TravelX: 207, TravelY: 206, new SpindleBlower(),
        [JobModes.Blow, JobModes.BedClean, JobModes.AntiStatic, JobModes.ExternalOutput, JobModes.TimeLapse]);

    public static readonly IReadOnlyList<MachineType> All = [Compact];

    /// <summary>Air of machines not listed here: the firmware's compressed-air switch.</summary>
    public static readonly IAirOutput DefaultAir = new CompressedAir();

    /// <summary>Job options of machines not listed here: the firmware's vacuum and external output modes.</summary>
    public static readonly IReadOnlyList<JobMode> DefaultJobModes = [JobModes.Vacuum, JobModes.ExternalOutput];

    public static MachineType? For(MachineTravel travel) => All.FirstOrDefault(t => t.Fits(travel));

    public static IAirOutput AirFor(MachineTravel travel) => For(travel)?.Air ?? DefaultAir;

    public static IReadOnlyList<JobMode> JobModesFor(MachineTravel travel) => For(travel)?.JobModes ?? DefaultJobModes;
}
