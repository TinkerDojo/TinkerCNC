using System.Globalization;

namespace TinkerCNC.Motion;

/// <summary>One straight move in machine coordinates and where it ends (X, Y, Z).</summary>
/// <param name="FeedMmPerMin">Its feed rate; for rapids the slowest speed a rapid is expected to have.</param>
public sealed record MachineMove(string Command, double[] Target, double FeedMmPerMin)
{
    /// <summary>Lower bound of a rapid's speed, used to estimate how long a <c>G0</c> move may take.</summary>
    public const double SlowestRapidMmPerMin = 1000;

    /// <summary>How long the move takes at its feed rate, starting from <paramref name="from"/>.</summary>
    public TimeSpan DurationFrom(double[] from)
    {
        var distance = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(Target[i] - from[i], 2)));
        return TimeSpan.FromMinutes(distance / FeedMmPerMin);
    }

    public static MachineMove Rapid(double x, double y, double z) =>
        new(Format($"G53 G0 X{x:0.###} Y{y:0.###} Z{z:0.###}"), [x, y, z], SlowestRapidMmPerMin);

    public static MachineMove Feed(double x, double y, double z, double feed) =>
        new(Format($"G53 G1 X{x:0.###} Y{y:0.###} Z{z:0.###} F{feed:0.###}"), [x, y, z], feed);

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>How the head sweeps the bed: lanes along X, <paramref name="LaneSpacingMm"/> apart in Y.</summary>
/// <param name="EdgeInsetMm">Distance kept from the soft limits.</param>
public sealed record BedSweepSettings(double LaneSpacingMm = 25, double EdgeInsetMm = 2, double FeedMmPerMin = 3000)
{
    public static readonly BedSweepSettings Default = new();
}

/// <summary>
/// Moves for cleaning the whole bed with the air on: the head goes up to the clearance height (the height the machine
/// itself crosses the bed at, so above clamps and stock), then sweeps the travel in lanes along X from the back
/// (Y max, where the machine homes) to the front, pushing chips forward. Afterwards it returns to where it started:
/// across at the clearance height, then straight down. Coming back that way matters for a paused program: the
/// firmware's <c>resume</c> returns to the stored position in one straight move, which from the clearance height would
/// cut diagonally through the stock (firmware, Player resume); once the head is back, that move is empty.
/// </summary>
public static class BedSweepPlanner
{
    /// <summary>Up to the clearance height (never down), staying above the start position.</summary>
    public static IReadOnlyList<MachineMove> Lift(double[] start, MachineTravel travel) =>
        start[2] >= travel.ClearanceZ ? [] : [MachineMove.Rapid(start[0], start[1], travel.ClearanceZ)];

    /// <summary>
    /// One pass over the bed at the height <paramref name="z"/>: a rapid to the back corner, then the lanes at the sweep feed.
    /// </summary>
    public static IReadOnlyList<MachineMove> Pass(MachineTravel travel, double z, BedSweepSettings settings)
    {
        var (left, right) = Inset(travel.X, settings.EdgeInsetMm);
        var lanes = Lanes(Inset(travel.Y, settings.EdgeInsetMm), settings.LaneSpacingMm);
        var moves = new List<MachineMove> { MachineMove.Rapid(right, lanes[0], z) };
        for (var i = 0; i < lanes.Count; i++)
        {
            var (from, to) = i % 2 == 0 ? (right, left) : (left, right);
            if (i > 0) moves.Add(MachineMove.Feed(from, lanes[i], z, settings.FeedMmPerMin));
            moves.Add(MachineMove.Feed(to, lanes[i], z, settings.FeedMmPerMin));
        }
        return moves;
    }

    /// <summary>Back to <paramref name="start"/>: across at the current (clearance) height, then straight down.</summary>
    public static IReadOnlyList<MachineMove> Return(double[] start, double z) =>
        start[2] >= z
            ? [MachineMove.Rapid(start[0], start[1], start[2])]
            : [MachineMove.Rapid(start[0], start[1], z), MachineMove.Rapid(start[0], start[1], start[2])];

    private static (double Min, double Max) Inset(AxisRange range, double inset) =>
        range.Length > 2 * inset ? (range.Min + inset, range.Max - inset) : (range.Min, range.Max);

    /// <summary>Evenly spaced lanes from the back (max) to the front (min), at most <paramref name="spacing"/> apart.</summary>
    private static List<double> Lanes((double Min, double Max) y, double spacing)
    {
        var count = Math.Max(1, (int)Math.Ceiling((y.Max - y.Min) / spacing)) + 1;
        var step = (y.Max - y.Min) / (count - 1);
        return Enumerable.Range(0, count).Select(i => i == count - 1 ? y.Min : y.Max - i * step).ToList();
    }
}
