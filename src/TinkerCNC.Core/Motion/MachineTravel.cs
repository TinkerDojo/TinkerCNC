using System.Globalization;

namespace TinkerCNC.Motion;

/// <summary>An axis' allowed range in machine coordinates (mm).</summary>
public sealed record AxisRange(double Min, double Max)
{
    public double Length => Max - Min;

    public bool Contains(double value) => value >= Min - Tolerance && value <= Max + Tolerance;

    /// <summary>Rounding of typed values must not reject a position exactly on the limit.</summary>
    private const double Tolerance = 1e-6;
}

/// <summary>
/// Where the tool can go, in machine coordinates. The machine homes to the maximum of each axis, which becomes the
/// position <c>alpha_max</c>/<c>beta_max</c>/<c>gamma_max</c> (0 by default); the soft limits
/// <c>soft_endstop.x_min</c>/<c>y_min</c>/<c>z_min</c> give the other end. The work bed's reference corner is
/// <c>coordinate.anchor1_x</c>/<c>_y</c>, and <c>coordinate.clearance_z</c> is the height the machine itself lifts to
/// before moving across the bed (setting names as in the firmware's config.txt).
/// </summary>
public sealed record MachineTravel(AxisRange X, AxisRange Y, AxisRange Z, double? BedCornerX, double? BedCornerY, double ClearanceZ)
{
    public static readonly IReadOnlyList<string> ConfigKeys =
    [
        "soft_endstop.x_min", "soft_endstop.y_min", "soft_endstop.z_min",
        "alpha_max", "beta_max", "gamma_max",
        "coordinate.anchor1_x", "coordinate.anchor1_y", "coordinate.clearance_z",
    ];

    /// <summary>Builds the travel from the configuration values; null when the soft limits are missing.</summary>
    public static MachineTravel? FromConfig(IReadOnlyDictionary<string, string?> values)
    {
        double? Get(string key) => values.GetValueOrDefault(key) is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        if (Get("soft_endstop.x_min") is not { } xMin || Get("soft_endstop.y_min") is not { } yMin || Get("soft_endstop.z_min") is not { } zMin)
            return null;
        var z = new AxisRange(zMin, Get("gamma_max") ?? 0);
        return new MachineTravel(
            new AxisRange(xMin, Get("alpha_max") ?? 0),
            new AxisRange(yMin, Get("beta_max") ?? 0),
            z,
            Get("coordinate.anchor1_x"),
            Get("coordinate.anchor1_y"),
            Math.Clamp(Get("coordinate.clearance_z") ?? z.Max, z.Min, z.Max));
    }

    public AxisRange this[Axis axis] => Ranges[axis](this);

    private static readonly IReadOnlyDictionary<Axis, Func<MachineTravel, AxisRange>> Ranges =
        new Dictionary<Axis, Func<MachineTravel, AxisRange>> { [Axis.X] = t => t.X, [Axis.Y] = t => t.Y, [Axis.Z] = t => t.Z };
}

public enum Axis { X, Y, Z }
