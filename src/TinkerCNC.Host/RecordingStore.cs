namespace TinkerCNC.Host;

/// <summary>
/// Where camera recordings (.mjpeg) and snapshots are written, and how they are named:
/// "&lt;label&gt;_&lt;timestamp&gt;.&lt;ext&gt;" with unsafe characters replaced.
/// The folder is <see cref="AppPaths.Recordings"/>.
/// </summary>
public sealed class RecordingStore(AppPaths paths)
{

    public string Directory { get; } = paths.Recordings;

    /// <summary>Creates a new recording file; raw JPEG frames appended to it form a playable MJPEG stream.</summary>
    public FileStream CreateRecording(string label) =>
        new(NewPath(label, "yyyyMMdd_HHmmss", "mjpeg"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);

    public string SaveSnapshot(string label, byte[] frame)
    {
        var path = NewPath(label, "yyyyMMdd_HHmmss_fff", FrameHub.ExtensionOf(frame));
        File.WriteAllBytes(path, frame);
        return path;
    }

    /// <summary>Full path of a stored file, or null if it does not exist. Only plain file names are accepted.</summary>
    public string? Find(string fileName)
    {
        var path = Path.Combine(Directory, Path.GetFileName(fileName));
        return File.Exists(path) ? path : null;
    }

    private string NewPath(string label, string timestampFormat, string extension)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var safe = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(Directory, $"{safe}_{DateTime.Now.ToString(timestampFormat)}.{extension}");
    }
}
