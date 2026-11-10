using TinkerCNC.Video;
using TinkerCNC.Video.Mp4;

namespace TinkerCNC.Host;

/// <summary>
/// Where camera recordings (.mp4) and snapshots are written and how they are named:
/// "&lt;label&gt;_&lt;timestamp&gt;.&lt;ext&gt;" with unsafe characters replaced. The folder is
/// <see cref="AppPaths.Recordings"/>. Recordings the app did not get to finish are completed by
/// <see cref="RecoverUnfinished"/> at the next start.
/// </summary>
public sealed class RecordingStore(AppPaths paths, IFrameDecoder decoder, ILogger<RecordingStore> log)
{
    public const string VideoExtension = "mp4";

    /// <summary>Recordings made before 0.3.0: raw JPEG frames one after the other.</summary>
    public const string LegacyVideoExtension = "mjpeg";

    public string Directory { get; } = paths.Recordings;

    /// <summary>Starts a recording into a new file named after the label and the time.</summary>
    public VideoRecorder CreateRecording(string label, RecordingPlan plan) =>
        new(NewPath(label, "yyyyMMdd_HHmmss", VideoExtension), plan, decoder, log);

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

    public static bool IsVideo(string path) =>
        path.EndsWith("." + VideoExtension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("." + LegacyVideoExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Completes recordings that were still running when the app last stopped: their part file gets the index
    /// built from the journal and its final name. Returns how many were completed.
    /// </summary>
    public int RecoverUnfinished()
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        var recovered = 0;
        var pattern = "*." + VideoExtension + VideoRecorder.PartSuffix + RecordingJournal.Suffix;
        foreach (var journal in System.IO.Directory.EnumerateFiles(Directory, pattern).ToList())
        {
            var part = journal[..^RecordingJournal.Suffix.Length];
            try
            {
                if (Recover(part, journal)) recovered++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "could not complete the recording {Part}", part);
            }
        }
        return recovered;
    }

    private bool Recover(string part, string journalPath)
    {
        if (!File.Exists(part))
        {
            File.Delete(journalPath);
            return false;
        }
        var (track, samples) = RecordingJournal.Read(journalPath);
        int kept;
        using (var file = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var writer = Mp4Writer.Resume(file, track, samples);
            kept = writer.Samples.Count;
            if (kept > 0) writer.Finish();
        }
        File.Delete(journalPath);
        if (kept == 0)
        {
            File.Delete(part);
            return false;
        }
        var final = Unique(part[..^VideoRecorder.PartSuffix.Length]);
        File.Move(part, final);
        log.LogInformation("completed the recording {Path} ({Frames} frames) that was cut short", final, kept);
        return true;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}_{n}{extension}";
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private string NewPath(string label, string timestampFormat, string extension)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var safe = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(Directory, $"{safe}_{DateTime.Now.ToString(timestampFormat)}.{extension}");
    }
}

/// <summary>Completes recordings cut short by the last shutdown, before anything else uses the folder.</summary>
internal sealed class RecordingRecovery(RecordingStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.RecoverUnfinished();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
