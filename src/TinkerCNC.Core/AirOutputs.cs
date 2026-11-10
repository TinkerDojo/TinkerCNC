using TinkerCNC.Localization;

namespace TinkerCNC;

/// <summary>How a machine blows chips away: the commands that switch its air on and off.</summary>
public interface IAirOutput
{
    /// <summary>What the air comes from, for showing to users.</summary>
    Text Description { get; }

    string OnCommand { get; }
    string OffCommand { get; }
}

/// <summary>
/// Compressed air (machines with an air output): <c>M7</c> on, <c>M9</c> off. It is the firmware's <c>air</c> switch; its state appears in
/// the reply to <c>diagnose</c> (field <c>R</c>), not in the regular status report.
/// </summary>
public sealed record CompressedAir : IAirOutput
{
    public Text Description => CoreTexts.Of("Air.Compressed");
    public string OnCommand => "M7";
    public string OffCommand => "M9";
}

/// <summary>
/// The blower around the spindle (compact machines): it is driven by the spindle fan output, <c>M811 S&lt;percent&gt;</c>
/// on and <c>M812</c> off, and blows chips away as well as cooling the spindle. <c>M7</c>/<c>M9</c> do nothing on these
/// machines.
/// </summary>
public sealed record SpindleBlower(int PowerPercent = 100) : IAirOutput
{
    public Text Description => CoreTexts.Of("Air.SpindleBlower", PowerPercent);
    public string OnCommand => FormattableString.Invariant($"M811 S{PowerPercent}");
    public string OffCommand => "M812";
}
