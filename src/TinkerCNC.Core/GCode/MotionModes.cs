namespace TinkerCNC.GCode;

public readonly record struct Point4(double X, double Y, double Z, double A)
{
    public double DistanceTo(Point4 other) =>
        Math.Sqrt(Sq(other.X - X) + Sq(other.Y - Y) + Sq(other.Z - Z) + Sq(other.A - A));

    /// <summary>
    /// Where this tool position lies on the part. The A axis turns the part about the machine's X axis, so a tool at
    /// (Y, Z) touches the point that sits at angle −A in the part's own frame; without rotation it is (X, Y, Z).
    /// Inferred: the rotation direction of a positive A move is assumed, not taken from the firmware.
    /// </summary>
    public Point3 OnPart()
    {
        if (A == 0) return new Point3(X, Y, Z);
        var radians = -A * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        return new Point3(X, Y * cos - Z * sin, Y * sin + Z * cos);
    }

    private static double Sq(double v) => v * v;
}

public readonly record struct Point3(double X, double Y, double Z);

/// <summary>A modal motion mode (G0–G3): how long a move to a target takes and which points it passes.</summary>
internal interface IMotionMode
{
    /// <summary>G1/G2/G3: the moves whose line the firmware reports while they run (block <c>is_g123</c>).</summary>
    bool IsFeed { get; }

    double Seconds(EstimatorState state, GCodeLine line, Point4 target);

    /// <summary>Points the move passes through after its start, ending at <paramref name="target"/>.</summary>
    IReadOnlyList<Point4> Path(EstimatorState state, GCodeLine line, Point4 target) => [target];
}

internal static class MotionModes
{
    public static readonly IMotionMode Rapid = new RapidMotion();
    public static readonly IMotionMode Linear = new LinearMotion();
    public static readonly IMotionMode ArcClockwise = new ArcMotion(clockwise: true);
    public static readonly IMotionMode ArcCounterClockwise = new ArcMotion(clockwise: false);

    public static double Seconds(double distanceMm, double feedMmPerMin) =>
        feedMmPerMin > 0 ? distanceMm / feedMmPerMin * 60 : 0;
}

/// <summary>G0 at the machine's rapid rate (acceleration is ignored).</summary>
internal sealed class RapidMotion : IMotionMode
{
    public bool IsFeed => false;

    public double Seconds(EstimatorState state, GCodeLine line, Point4 target) =>
        MotionModes.Seconds(state.Position.DistanceTo(target), state.Settings.RapidFeedMmPerMin);
}

/// <summary>G1 at the programmed feed.</summary>
internal sealed class LinearMotion : IMotionMode
{
    public bool IsFeed => true;

    public double Seconds(EstimatorState state, GCodeLine line, Point4 target) =>
        MotionModes.Seconds(state.Position.DistanceTo(target), state.EffectiveFeed);
}

/// <summary>G2/G3 in the XY plane, with I/J centre offsets or an R radius; helical Z (and A) is interpolated.</summary>
internal sealed class ArcMotion(bool clockwise) : IMotionMode
{
    /// <summary>Largest angle drawn as one chord.</summary>
    private const double MaxChordRadians = Math.PI / 36; // 5°

    public bool IsFeed => true;

    public double Seconds(EstimatorState state, GCodeLine line, Point4 target)
    {
        var start = state.Position;
        var sweepLength = ArcOf(state, line, target) is { } arc ? arc.Radius * Math.Abs(arc.Sweep) : Chord(start, target);
        var length = Math.Sqrt(sweepLength * sweepLength + Math.Pow(target.Z - start.Z, 2));
        return MotionModes.Seconds(length, state.EffectiveFeed);
    }

    public IReadOnlyList<Point4> Path(EstimatorState state, GCodeLine line, Point4 target)
    {
        if (ArcOf(state, line, target) is not { } arc) return [target];
        var start = state.Position;
        var chords = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.Sweep) / MaxChordRadians));
        return Enumerable.Range(1, chords)
            .Select(k => k == chords ? target : arc.PointAt((double)k / chords, start, target))
            .ToList();
    }

    /// <summary>The arc's geometry, or null for a degenerate arc (zero radius), which moves in a straight line.</summary>
    private Arc? ArcOf(EstimatorState state, GCodeLine line, Point4 target)
    {
        var start = state.Position;
        var (i, j) = line.Has('R')
            ? CentreOfRadius(start, target, line['R']!.Value * state.UnitScale)
            : ((line['I'] ?? 0) * state.UnitScale, (line['J'] ?? 0) * state.UnitScale);
        var radius = Math.Sqrt(i * i + j * j);
        if (radius < 1e-9) return null;

        var cx = start.X + i;
        var cy = start.Y + j;
        var a0 = Math.Atan2(start.Y - cy, start.X - cx);
        var a1 = Math.Atan2(target.Y - cy, target.X - cx);
        var sweep = clockwise ? a0 - a1 : a1 - a0;
        sweep = ((sweep % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        if (sweep < 1e-9) sweep = 2 * Math.PI; // equal start and end: full circle
        return new Arc(cx, cy, radius, a0, clockwise ? -sweep : sweep);
    }

    /// <summary>
    /// Centre offset (I, J) of an R-format arc, as Grbl's gc_execute_line computes it: the centre lies on the chord's
    /// perpendicular bisector, on the side given by the direction; a negative R selects the arc larger than 180°.
    /// </summary>
    private (double I, double J) CentreOfRadius(Point4 start, Point4 end, double r)
    {
        var x = end.X - start.X;
        var y = end.Y - start.Y;
        var chord = Math.Sqrt(x * x + y * y);
        if (chord < 1e-9) return (0, 0);
        var h = -Math.Sqrt(Math.Max(0, 4 * r * r - chord * chord)) / chord;
        if (!clockwise) h = -h;
        if (r < 0) h = -h;
        return (0.5 * (x - y * h), 0.5 * (y + x * h));
    }

    private static double Chord(Point4 a, Point4 b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    /// <param name="Sweep">Signed angle from the start to the end (negative = clockwise).</param>
    private sealed record Arc(double CentreX, double CentreY, double Radius, double StartAngle, double Sweep)
    {
        public Point4 PointAt(double fraction, Point4 start, Point4 end)
        {
            var angle = StartAngle + Sweep * fraction;
            return new Point4(
                CentreX + Radius * Math.Cos(angle),
                CentreY + Radius * Math.Sin(angle),
                start.Z + (end.Z - start.Z) * fraction,
                start.A + (end.A - start.A) * fraction);
        }
    }
}
