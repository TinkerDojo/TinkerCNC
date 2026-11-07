namespace TinkerCNC.Programs;

/// <summary>
/// Names of the copies kept in the data folder (setup files, queued files): the original name made safe, with a
/// GUID before the extension, so files of the same name can coexist.
/// </summary>
public static class StoredFiles
{
    /// <summary>Not allowed in file names on any system the app runs on.</summary>
    private static readonly char[] InvalidFileNameChars = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Directories are stripped and the name split by hand, because <see cref="Path"/> treats ':' as a separator on
    /// Windows only and the result must be the same on every system.
    /// </summary>
    public static string StoredFileName(string originalFileName)
    {
        var safe = new string(FileNameOf(originalFileName).Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray());
        var dot = safe.LastIndexOf('.');
        var (stem, extension) = dot < 0 ? (safe, "") : (safe[..dot], safe[dot..]);
        if (extension == ".") extension = ""; // a trailing dot is dropped by Windows file systems
        return $"{(stem.Length > 0 ? stem : "program")}_{Guid.NewGuid():N}{extension}";
    }

    /// <summary>The part after the last '/' or '\\', whatever system the path came from.</summary>
    public static string FileNameOf(string path) => path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

    /// <summary>Writes a copy of <paramref name="content"/> into <paramref name="directory"/> and returns its file name.</summary>
    public static string Store(string directory, string originalFileName, byte[] content)
    {
        var stored = StoredFileName(originalFileName);
        File.WriteAllBytes(Path.Combine(directory, stored), content);
        return stored;
    }
}
