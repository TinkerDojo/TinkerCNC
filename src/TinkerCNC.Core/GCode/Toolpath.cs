namespace TinkerCNC.GCode;

/// <summary>How a toolpath point is reached from the point before it.</summary>
public enum ToolpathMove : byte
{
    /// <summary>Not drawn: the position before it is unknown (program start, after a tool change, after G92).</summary>
    Jump,
    /// <summary>G0.</summary>
    Rapid,
    /// <summary>G1/G2/G3 (arcs are split into short chords).</summary>
    Feed,
}

/// <summary>
/// The program's moves as one polyline on the part (see <see cref="Point4.OnPart"/>): point <c>k</c> is reached from
/// point <c>k − 1</c> by <see cref="Moves"/>[k] while line <see cref="Lines"/>[k] runs. Lines never decrease, so the
/// part already cut up to a line is a prefix of the points.
/// </summary>
public sealed class Toolpath
{
    private Toolpath(float[] positions, int[] lines, ToolpathMove[] moves)
    {
        Positions = positions;
        Lines = lines;
        Moves = moves;
    }

    /// <summary>X, Y, Z of every point in millimetres, interleaved.</summary>
    public float[] Positions { get; }

    /// <summary>1-based file line of the move ending at every point.</summary>
    public int[] Lines { get; }

    public ToolpathMove[] Moves { get; }

    public int Count => Lines.Length;

    /// <summary>The box around every point, or null for an empty path.</summary>
    public ToolpathBounds? Bounds() => BoundsOf(Enumerable.Range(0, Count));

    /// <summary>
    /// The box around the cutting moves (G1/G2/G3, start and end), leaving out rapids such as the move to a safe
    /// height or a parking position; null without any cutting move.
    /// </summary>
    public ToolpathBounds? CutBounds() =>
        BoundsOf(Enumerable.Range(1, Math.Max(0, Count - 1)).Where(k => Moves[k] == ToolpathMove.Feed).SelectMany(k => new[] { k - 1, k }));

    private ToolpathBounds? BoundsOf(IEnumerable<int> points)
    {
        double[] min = [double.MaxValue, double.MaxValue, double.MaxValue], max = [double.MinValue, double.MinValue, double.MinValue];
        var any = false;
        foreach (var k in points)
        {
            any = true;
            for (var a = 0; a < 3; a++)
            {
                var v = Positions[3 * k + a];
                min[a] = Math.Min(min[a], v);
                max[a] = Math.Max(max[a], v);
            }
        }
        return any ? new ToolpathBounds(min[0], min[1], min[2], max[0], max[1], max[2]) : null;
    }

    internal static Toolpath Build(IReadOnlyList<LineStep> steps)
    {
        var builder = new Builder();
        var previousEnd = default(Point4);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.ToolChanged) builder.Forget(); // the changer moved the head somewhere we cannot see
            if (step.Path.Count > 0) builder.Move(previousEnd, step.Path, i + 1, step.FeedMove ? ToolpathMove.Feed : ToolpathMove.Rapid);
            previousEnd = step.EndPosition;
        }
        return builder.ToToolpath();
    }

    private sealed class Builder
    {
        private readonly List<float> _positions = new();
        private readonly List<int> _lines = new();
        private readonly List<ToolpathMove> _moves = new();
        private Point4? _last;

        public void Forget() => _last = null;

        public void Move(Point4 start, IReadOnlyList<Point4> path, int line, ToolpathMove move)
        {
            if (_last is null)
            {
                Add(path[^1], line, ToolpathMove.Jump); // the first move's start is unknown: show only where it ends
                return;
            }
            if (_last != start) Add(start, line, ToolpathMove.Jump); // G92 redefined the position
            foreach (var point in path) Add(point, line, move);
        }

        public Toolpath ToToolpath() => new(_positions.ToArray(), _lines.ToArray(), _moves.ToArray());

        private void Add(Point4 point, int line, ToolpathMove move)
        {
            var onPart = point.OnPart();
            _positions.Add((float)onPart.X);
            _positions.Add((float)onPart.Y);
            _positions.Add((float)onPart.Z);
            _lines.Add(line);
            _moves.Add(move);
            _last = point;
        }
    }
}

/// <summary>A box in the program's coordinates (mm).</summary>
public sealed record ToolpathBounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public double Width => MaxX - MinX;
    public double Depth => MaxY - MinY;

    /// <summary>The same box moved by <paramref name="offset"/> (X, Y, Z), e.g. from work to machine coordinates.</summary>
    public ToolpathBounds Offset(double[] offset) => new(
        MinX + offset[0], MinY + offset[1], MinZ + offset[2], MaxX + offset[0], MaxY + offset[1], MaxZ + offset[2]);
}
