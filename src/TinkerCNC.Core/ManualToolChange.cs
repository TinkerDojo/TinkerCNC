using TinkerCNC.Localization;

namespace TinkerCNC;

/// <summary>
/// Tool changes on machines without an automatic tool changer. On <c>M6 Tn</c> the firmware moves to the tool change
/// position, prints "Please change the tool to: Tn", records n as the target tool and runs <c>M490.1</c>, which
/// waits: the state becomes <c>Tool</c> until <c>M490.2</c> arrives, then the tool length is calibrated and the
/// program continues (firmware, ATCHandler fill_change_scripts and M490). A host confirms the change by
/// sending <c>M490.2</c>.
/// </summary>
public static class ManualToolChange
{
    /// <summary>Status state while the machine waits for the tool.</summary>
    public const string WaitingState = "Tool";

    /// <summary>Ends the wait.</summary>
    public const string ConfirmCommand = "M490.2";

    // Tool numbers the firmware reserves: 0 for the probe, 8888 for the laser module, 9999 for the 3D probe.
    private static readonly IReadOnlyDictionary<int, Text> SpecialTools = new Dictionary<int, Text>
    {
        [0] = CoreTexts.Of("Tool.Probe"),
        [8888] = CoreTexts.Of("Tool.Laser"),
        [9999] = CoreTexts.Of("Tool.Probe3D"),
    };

    /// <summary>Short name of a tool for prompts: "T3", or what a reserved number stands for ("the probe").</summary>
    public static Text NameOf(int tool) => SpecialTools.GetValueOrDefault(tool) ?? Text.Plain($"T{tool}");

    /// <summary>A real cutting tool: not "none" (−1) and none of the reserved numbers.</summary>
    public static bool IsCuttingTool(int tool) => tool > 0 && !SpecialTools.ContainsKey(tool);

    public static bool IsWaiting(StatusReport? status) =>
        string.Equals(status?.State, WaitingState, StringComparison.OrdinalIgnoreCase);
}
