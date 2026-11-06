namespace TinkerCNC.Web.Components.Job;

/// <summary>Stable colour per tool number, so a tool keeps its colour across the timeline, legend and viewer.</summary>
public static class ToolPalette
{
    private const string NoTool = "#5b6573";

    private static readonly string[] Colors =
    {
        "#f08a24", "#3fa7ff", "#3ccf8e", "#c678dd", "#f5c04a", "#ff6b81",
        "#2ec4b6", "#9b8cff", "#ff9f68", "#7bd389", "#e5c07b", "#56b6c2",
    };

    public static string ColorFor(int? tool) => tool is { } n ? Colors[Math.Abs(n) % Colors.Length] : NoTool;
}
