namespace TinkerCNC.Motion;

/// <summary>The units of the commands the host writes itself (go-to, jogs, bed cleaning, the queue's gate).</summary>
public static class MachineUnits
{
    /// <summary>
    /// <c>G21</c>: the host's own moves are in millimetres. The machine keeps the units the last program left behind
    /// (<c>G20</c> after an inch program), in which a move typed in millimetres would go 25.4 times as far, so every
    /// sequence of the host's own begins with this.
    /// </summary>
    public const string Millimetres = "G21";
}
