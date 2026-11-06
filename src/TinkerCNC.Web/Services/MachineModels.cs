using TinkerCNC.Motion;

namespace TinkerCNC.Web.Services;

/// <summary>
/// A 3D model of a machine for the machine views (mm, Z up), shipped in <c>wwwroot/models</c>. Its part names carry
/// roles: <c>moving__</c> parts carry the spindle and follow the machine position, <c>shell__</c> parts are the
/// enclosure and are drawn from the inside.
/// </summary>
/// <param name="AnchorX">The L-bracket's inner corner (the machine's <c>coordinate.anchor1</c>) in model coordinates.</param>
/// <param name="BedTopZ">Height of the bed's top surface in model coordinates.</param>
/// <param name="Nose">Spindle nose (centre, lowest point) in model coordinates, as the model shows the spindle; null when
/// the model has no spindle.</param>
public sealed record ModelSource(string Url, double AnchorX, double AnchorY, double BedTopZ, double[]? Nose = null)
{
    /// <summary>
    /// Machine coordinates of the model's origin: its anchor lands on the machine's anchor1 and its bed surface on the
    /// lowest Z the machine reaches (approximate: the real height depends on tool and spoilboard).
    /// </summary>
    public double[] OriginIn(MachineTravel travel) =>
        [travel.BedCornerX!.Value - AnchorX, travel.BedCornerY!.Value - AnchorY, travel.Z.Min - BedTopZ];
}

/// <summary>A machine the views can show as a model, recognised by its travel (<see cref="MachineType"/>).</summary>
public sealed record MachineModel(MachineType Type, ModelSource Model)
{
    public string Name => Type.Name;

    /// <summary>The model is placed by the bed corner, so that has to be known too.</summary>
    public bool Fits(MachineTravel travel) =>
        travel.BedCornerX is not null && travel.BedCornerY is not null && Type.Fits(travel);
}

public static class MachineModels
{
    public static readonly IReadOnlyList<MachineModel> All =
    [
        // Bed CAD by cinetronix (CC BY 4.0, see THIRD-PARTY-NOTICES.md): aluminium base, MDF bed and L-bracket,
        // converted once. L-bracket inner corner and MDF top were measured in that model.
        new(MachineTypes.Compact, new ModelSource("models/compact-bed.glb", AnchorX: -88, AnchorY: -88, BedTopZ: 6)),
    ];

    public static MachineModel? For(MachineTravel travel) => All.FirstOrDefault(m => m.Fits(travel));
}
