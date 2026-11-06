namespace TinkerCNC.GCode;

/// <summary>Duration of every program line and the queries the job view needs (lines are 1-based).</summary>
public sealed class LineTimes
{
    private readonly double[] _seconds;
    private readonly double[] _cumulative; // end time of each line

    public LineTimes(IReadOnlyList<double> seconds)
    {
        _seconds = seconds.ToArray();
        _cumulative = new double[_seconds.Length];
        var sum = 0.0;
        for (var i = 0; i < _seconds.Length; i++) _cumulative[i] = sum += _seconds[i];
    }

    public IReadOnlyList<double> Seconds => _seconds;
    public int Count => _seconds.Length;
    public double Total => _cumulative.Length == 0 ? 0 : _cumulative[^1];

    /// <summary>Time spent before <paramref name="line"/> starts.</summary>
    public double SecondsBefore(int line) =>
        line <= 1 || _cumulative.Length == 0 ? 0 : _cumulative[Math.Min(line, _cumulative.Length + 1) - 2];

    /// <summary>Total time of the inclusive line range.</summary>
    public double Between(int firstLine, int lastLine) => SecondsBefore(lastLine + 1) - SecondsBefore(firstLine);

    /// <summary>
    /// Time from the start of <paramref name="fromLine"/> to the start of <paramref name="toLine"/>, scaled by the
    /// feed override.
    /// </summary>
    public double SecondsUntil(int fromLine, int toLine, double feedOverridePercent = 100) =>
        (SecondsBefore(toLine) - SecondsBefore(fromLine)) / (Math.Max(feedOverridePercent, 1) / 100.0);

    /// <summary>Remaining time from <paramref name="line"/> to the end of the program.</summary>
    public double Remaining(int line, double feedOverridePercent = 100) => SecondsUntil(line, Count + 1, feedOverridePercent);

    public double Fraction(int line) => Total > 0 ? Math.Clamp(SecondsBefore(line) / Total, 0, 1) : 0;
}
