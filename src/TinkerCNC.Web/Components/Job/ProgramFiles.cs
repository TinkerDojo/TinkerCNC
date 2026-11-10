using Microsoft.AspNetCore.Components.Forms;
using TinkerCNC.Localization;
using TinkerCNC.Web.Localization;

namespace TinkerCNC.Web.Components.Job;

/// <summary>A G-code file chosen in the browser, read into memory.</summary>
public sealed record ChosenFile(string Name, byte[] Content);

/// <summary>G-code files chosen in the browser.</summary>
public static class ProgramFiles
{
    /// <summary>File types offered when choosing a G-code file.</summary>
    public const string Accept = ".nc,.gcode,.ngc,.tap,.txt,.cnc";

    /// <summary>The <see cref="Accept"/> types in words, to show with <c>T[…]</c>.</summary>
    public static Text AcceptText { get; } = Texts.Of("Setups.Accept");

    private const long MaxSize = 100 * 1024 * 1024;

    /// <summary>Files taken from one choice of several.</summary>
    public const int MaxFiles = 200;

    public static async Task<ChosenFile> ReadAsync(IBrowserFile file)
    {
        using var buffer = new MemoryStream();
        await file.OpenReadStream(maxAllowedSize: MaxSize).CopyToAsync(buffer);
        return new ChosenFile(Path.GetFileName(file.Name), buffer.ToArray());
    }

    /// <summary>Every file of a multiple-file choice, in the order chosen.</summary>
    public static async Task<IReadOnlyList<ChosenFile>> ReadAllAsync(InputFileChangeEventArgs e)
    {
        var files = new List<ChosenFile>();
        foreach (var file in e.GetMultipleFiles(MaxFiles)) files.Add(await ReadAsync(file));
        return files;
    }

    public static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:0.0} MB" : $"{Math.Max(1, bytes / 1024)} kB";
}
