namespace TinkerCNC.Web.Components.Machine;

/// <summary>
/// The position the Go-to inputs would move to (machine coordinates), shared with the machine view so the target is
/// visible before moving. Null while nothing valid is entered.
/// </summary>
public sealed class MoveTargetPreview
{
    public double[]? Target { get; private set; }

    public event Action? Changed;

    public void Show(double[]? target)
    {
        if (Target == target || (Target is not null && target is not null && Target.SequenceEqual(target))) return;
        Target = target;
        Changed?.Invoke();
    }
}
