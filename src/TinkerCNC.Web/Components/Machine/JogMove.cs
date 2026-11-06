using TinkerCNC.Host;
namespace TinkerCNC.Web.Components.Machine;

/// <summary>A jog button: direction per axis (multiplied by the selected step) and its place on the pad.</summary>
public sealed record JogMove(string Label, string Icon, string Slot, double X = 0, double Y = 0, double Z = 0, double A = 0)
{
    public Task ExecuteAsync(MachineSession session, double step) =>
        session.JogAsync(X * step, Y * step, Z * step, A * step);

    public static readonly IReadOnlyList<JogMove> All =
    [
        new("Y+", "up", "y-plus", Y: 1),
        new("X−", "left", "x-minus", X: -1),
        new("X+", "right", "x-plus", X: 1),
        new("Y−", "down", "y-minus", Y: -1),
        new("Z+", "up", "z-plus", Z: 1),
        new("Z−", "down", "z-minus", Z: -1),
        new("A+", "rotate", "a-plus", A: 1),
        new("A−", "rotate", "a-minus", A: -1),
    ];
}
