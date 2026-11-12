using System.Globalization;
using TinkerCNC.Localization;

namespace TinkerCNC.Motion;

/// <summary>The coordinate system of typed positions.</summary>
public enum CoordinateFrame
{
    /// <summary>Relative to the work origin (WPos), like the program.</summary>
    Work,
    /// <summary>The machine's own coordinates (MPos).</summary>
    Machine,
}

/// <summary>A position to move to; axes left empty keep their current value.</summary>
/// <param name="LiftFirst">
/// Raise to the clearance height, move in X/Y, then lower to Z (instead of one straight move). Without a Z the tool
/// stays at the clearance height: going back down at a new place could run into the stock.
/// </param>
public sealed record GoToRequest(double? X, double? Y, double? Z, CoordinateFrame Frame, bool LiftFirst = true)
{
    public double? this[Axis axis] => Values[axis](this);

    private static readonly IReadOnlyDictionary<Axis, Func<GoToRequest, double?>> Values =
        new Dictionary<Axis, Func<GoToRequest, double?>> { [Axis.X] = r => r.X, [Axis.Y] = r => r.Y, [Axis.Z] = r => r.Z };
}

/// <param name="Commands">What to send, in order; empty when the request is invalid.</param>
/// <param name="Errors">Why the request is invalid, per axis.</param>
/// <param name="Target">The resulting position in machine coordinates (X, Y, Z).</param>
public sealed record GoToPlan(IReadOnlyList<string> Commands, IReadOnlyDictionary<Axis, Text> Errors, double[] Target)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Turns a typed position into rapid moves in machine coordinates (<c>G53 G0</c>), refusing anything outside the
/// machine's travel. Work coordinates are converted with the current offset between MPos and WPos.
/// </summary>
public static class GoToPlanner
{
    private static readonly Axis[] Axes = [Axis.X, Axis.Y, Axis.Z];

    private static readonly IReadOnlyDictionary<CoordinateFrame, Text> FrameNames = new Dictionary<CoordinateFrame, Text>
    {
        [CoordinateFrame.Work] = CoreTexts.Of("GoTo.WorkCoordinates"),
        [CoordinateFrame.Machine] = CoreTexts.Of("GoTo.MachineCoordinates"),
    };

    /// <summary>The range of an axis in the given frame (for showing limits next to the inputs).</summary>
    public static AxisRange RangeIn(CoordinateFrame frame, Axis axis, MachineTravel travel, StatusReport status)
    {
        var offset = OffsetOf(frame, status)[(int)axis];
        var range = travel[axis];
        return new AxisRange(range.Min - offset, range.Max - offset);
    }

    public static GoToPlan Plan(GoToRequest request, StatusReport status, MachineTravel travel)
    {
        var current = status.MachinePosition is { Length: >= 3 } m ? m : null;
        if (current is null)
            return Invalid(Axis.X, CoreTexts.Of("GoTo.NoPosition"), [0, 0, 0]);
        if (request.Frame == CoordinateFrame.Work && status.WorkPosition is not { Length: >= 3 })
            return Invalid(Axis.X, CoreTexts.Of("GoTo.NoWorkPosition"), current[..3]);

        var offset = OffsetOf(request.Frame, status);
        var target = Axes.Select(a => request[a] is { } v ? v + offset[(int)a] : current[(int)a]).ToArray();
        var errors = Axes
            .Where(a => request[a] is not null && !travel[a].Contains(target[(int)a]))
            .ToDictionary(a => a, a => OutOfRange(a, request.Frame, RangeIn(request.Frame, a, travel, status)));
        if (errors.Count > 0) return new GoToPlan([], errors, target);

        var lifted = request.LiftFirst && (request.X is not null || request.Y is not null);
        if (lifted && request.Z is null) target[2] = Math.Max(travel.ClearanceZ, current[2]);
        return new GoToPlan(lifted ? LiftedMoves(request, current, target, travel) : [.. Absolute, Move(target[0], target[1], target[2])], errors, target);
    }

    /// <summary>Millimetres (<see cref="MachineUnits"/>) and absolute positions, whatever the last program left.</summary>
    private static readonly string[] Absolute = [MachineUnits.Millimetres, "G90"];

    /// <summary>Up to the clearance height (never down), across, then down to the requested Z.</summary>
    private static List<string> LiftedMoves(GoToRequest request, double[] current, double[] target, MachineTravel travel)
    {
        var clearance = Math.Max(travel.ClearanceZ, current[2]);
        var moves = new List<string>(Absolute);
        if (clearance > current[2]) moves.Add(Move(z: clearance));
        moves.Add(Move(x: target[0], y: target[1]));
        if (request.Z is not null) moves.Add(Move(z: target[2]));
        return moves;
    }

    private static string Move(double? x = null, double? y = null, double? z = null)
    {
        static string W(char axis, double? v) => v is { } value ? string.Create(CultureInfo.InvariantCulture, $" {axis}{value:0.###}") : "";
        return "G53 G0" + W('X', x) + W('Y', y) + W('Z', z);
    }

    /// <summary>
    /// Machine position − typed position: 0 for machine coordinates, MPos − WPos for work coordinates (0 while WPos is
    /// not reported, for showing ranges only: <see cref="Plan"/> refuses work coordinates then).
    /// </summary>
    private static double[] OffsetOf(CoordinateFrame frame, StatusReport status) =>
        frame == CoordinateFrame.Machine || status.MachinePosition is not { Length: >= 3 } m || status.WorkPosition is not { Length: >= 3 } w
            ? [0, 0, 0]
            : [m[0] - w[0], m[1] - w[1], m[2] - w[2]];

    private static Text OutOfRange(Axis axis, CoordinateFrame frame, AxisRange range) =>
        CoreTexts.Of("GoTo.OutOfRange", axis.ToString(), Number(range.Min), Number(range.Max), FrameNames[frame]);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static GoToPlan Invalid(Axis axis, Text error, double[] target) =>
        new([], new Dictionary<Axis, Text> { [axis] = error }, target);
}
