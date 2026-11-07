using System.Text.Json.Serialization;
using TinkerCNC.GCode;

namespace TinkerCNC.Jobs;

/// <summary>Where the program's zero (X/Y) is put before it starts.</summary>
public enum WorkOriginChoice
{
    /// <summary>Leave the work origin as it is.</summary>
    Keep,
    /// <summary>The bed's reference corner (anchor 1) plus an offset.</summary>
    BedCorner,
    /// <summary>Where the head is now, plus an offset.</summary>
    HeadPosition,
    /// <summary>Probe the stock's corner with the XYZ probe block, from where the tool is.</summary>
    XyzProbe,
}

/// <summary>
/// Everything that can be chosen before starting a program. Plain settable properties so a form can bind to them and
/// they can be saved as JSON; <see cref="JobStartPlan"/> turns them into commands.
/// </summary>
public sealed class JobStartOptions
{
    public const int MinLevelingPoints = 2;
    public const int MaxLevelingPoints = 15;

    public WorkOriginChoice WorkOrigin { get; set; } = WorkOriginChoice.Keep;

    /// <summary>Offset (mm) from the bed corner or the head position.</summary>
    public double OriginOffsetX { get; set; }
    public double OriginOffsetY { get; set; }

    /// <summary>XYZ probe block: its height over the stock (mm) and the diameter of the tool touching it (mm).</summary>
    public double XyzProbeHeight { get; set; } = 9;
    public double XyzProbeToolDiameter { get; set; } = 3.175;

    /// <summary>Trace the program's outline at the clearance height before starting, to check it fits the stock.</summary>
    public bool ScanMargin { get; set; }

    /// <summary>Measure the stock's top with the probe and make it Z zero.</summary>
    public bool ZProbe { get; set; }

    /// <summary>
    /// Whether <see cref="ZProbeOffsetX"/>/<see cref="ZProbeOffsetY"/> count from the work origin instead of the
    /// program's corner (the path origin: the front left corner of its cutting moves).
    /// </summary>
    public bool ZProbeFromWorkOrigin { get; set; }
    public double ZProbeOffsetX { get; set; }
    public double ZProbeOffsetY { get; set; }

    /// <summary>Probe a grid over the program's area and follow the measured surface.</summary>
    public bool Leveling { get; set; }
    public int LevelingPointsX { get; set; } = 5;
    public int LevelingPointsY { get; set; } = 5;

    /// <summary>Height above the surface the probe moves at between grid points (mm).</summary>
    public double LevelingHeight { get; set; } = 5;

    /// <summary>Job options by <see cref="JobMode.Key"/>; missing ones are off.</summary>
    public Dictionary<string, bool> Modes { get; set; } = new();

    /// <summary>
    /// Record a time-lapse of the job with the machine's camera: one frame every few seconds from the start to the
    /// end of playback, written to the recordings folder here. Nothing is sent to the machine for it.
    /// </summary>
    public bool Timelapse { get; set; }

    [JsonIgnore]
    public bool UsesProbe => ScanMargin || ZProbe || Leveling;

    public bool IsOn(JobMode mode) => Modes.GetValueOrDefault(mode.Key);

    /// <summary>Probes the stock's top in the middle of the program's area: half its size from its corner.</summary>
    public void ZProbeAtCenterOf(ToolpathBounds program)
    {
        ZProbeFromWorkOrigin = false;
        ZProbeOffsetX = Math.Round(program.Width / 2, 3);
        ZProbeOffsetY = Math.Round(program.Depth / 2, 3);
    }

    /// <summary>Changes what the Z probe offset counts from; with a program the probing point stays where it is.</summary>
    public void SetZProbeFromWorkOrigin(bool fromWorkOrigin, ToolpathBounds? program)
    {
        if (fromWorkOrigin == ZProbeFromWorkOrigin) return;
        if (program is not null)
        {
            var (x, y) = fromWorkOrigin ? ZProbePoint(program) : ZProbeFromCorner(program);
            ZProbeOffsetX = Math.Round(x, 3);
            ZProbeOffsetY = Math.Round(y, 3);
        }
        ZProbeFromWorkOrigin = fromWorkOrigin;
    }

    /// <summary>Where the Z probe touches, relative to the program's corner (what the firmware takes).</summary>
    public (double X, double Y) ZProbeFromCorner(ToolpathBounds program) =>
        ZProbeFromWorkOrigin
            ? (ZProbeOffsetX - program.MinX, ZProbeOffsetY - program.MinY)
            : (ZProbeOffsetX, ZProbeOffsetY);

    /// <summary>Whether the Z probe touches within the area of the program's cutting moves.</summary>
    public bool ZProbeWithin(ToolpathBounds program)
    {
        var (x, y) = ZProbeFromCorner(program);
        return x >= 0 && y >= 0 && x <= program.Width && y <= program.Depth;
    }

    /// <summary>Where the Z probe touches, in work coordinates.</summary>
    public (double X, double Y) ZProbePoint(ToolpathBounds program)
    {
        var (x, y) = ZProbeFromCorner(program);
        return (x + program.MinX, y + program.MinY);
    }

    public JobStartOptions Clone()
    {
        var copy = (JobStartOptions)MemberwiseClone();
        copy.Modes = new Dictionary<string, bool>(Modes);
        return copy;
    }
}
