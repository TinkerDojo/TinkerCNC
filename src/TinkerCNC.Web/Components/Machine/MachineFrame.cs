using TinkerCNC.Motion;
using TinkerCNC.Web.Services;

namespace TinkerCNC.Web.Components.Machine;

/// <summary>
/// The machine as the 3D views draw it (<c>wwwroot/js/machine-scene.js</c>): its travel in machine coordinates, the
/// clearance height and, when it is recognised by its travel, its model.
/// </summary>
public sealed record MachineFrame(double[] Min, double[] Max, double ClearanceZ, MachineFrame.ModelState? Model)
{
    /// <summary>The machine's model and where its origin goes (machine coordinates).</summary>
    public sealed record ModelState(string Name, string Url, double[] Origin, double[]? Nose);

    public static MachineFrame For(MachineTravel travel) => new(
        [travel.X.Min, travel.Y.Min, travel.Z.Min], [travel.X.Max, travel.Y.Max, travel.Z.Max], travel.ClearanceZ,
        MachineModels.For(travel) is { } model ? ModelOf(model, travel) : null);

    public static ModelState ModelOf(MachineModel model, MachineTravel travel) =>
        new(model.Name, model.Model.Url, model.Model.OriginIn(travel), model.Model.Nose);

    /// <summary>
    /// How high the stock is drawn: from the bed (at the lowest Z the spindle reaches, as the views draw it; the machine
    /// reports no bed height) up to work zero, the stock's probed top. Null when work zero is unknown or not above it.
    /// </summary>
    public static double? StockHeight(StatusReport? status, MachineTravel travel) =>
        status?.WorkZero is { } zero && zero[2] - travel.Z.Min is var height && height > 0 ? height : null;

    /// <summary>Whether <paramref name="other"/> draws the same machine (the arrays compared by value).</summary>
    public bool SameAs(MachineFrame? other) =>
        other is not null && Min.SequenceEqual(other.Min) && Max.SequenceEqual(other.Max) && ClearanceZ == other.ClearanceZ
        && Model?.Url == other.Model?.Url && (Model?.Origin ?? []).SequenceEqual(other.Model?.Origin ?? []);
}
