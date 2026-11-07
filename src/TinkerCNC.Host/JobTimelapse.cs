using TinkerCNC.Jobs;

namespace TinkerCNC.Host;

/// <summary>
/// Records a time-lapse of a job started here (<see cref="JobStartOptions.Timelapse"/>) with the machine's camera:
/// from the moment the machine plays until it stops playing, one frame every <see cref="IntervalFor"/> into a
/// recording in the recordings folder (<see cref="CameraService.StartRecording"/>). The camera is started if it is
/// off. A recording that was already running is left alone.
/// </summary>
public sealed class JobTimelapse
{
    /// <summary>How many frames a time-lapse aims for: at the player's 10 frames per second, half a minute.</summary>
    public const int TargetFrames = 300;

    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    private readonly MachineSession _session;
    private readonly CameraService _camera;
    private bool _wasPlaying;

    public JobTimelapse(MachineSession session, CameraService camera)
    {
        _session = session;
        _camera = camera;
        _session.Changed += OnSessionChanged;
    }

    /// <summary>Whether a time-lapse of the running job is being recorded.</summary>
    public bool Recording { get; private set; }

    /// <summary>Where the last time-lapse was written, once the job ended.</summary>
    public string? LastPath { get; private set; }

    /// <summary>Why the last time-lapse could not be recorded, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Time between frames for a job of that length, so the result has about <see cref="TargetFrames"/>.</summary>
    public static TimeSpan IntervalFor(double estimatedSeconds)
    {
        var seconds = Math.Clamp(estimatedSeconds / TargetFrames, MinInterval.TotalSeconds, MaxInterval.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Round(seconds, 1));
    }

    /// <summary>Starts recording the job that is playing now.</summary>
    /// <param name="label">Start of the file name (the program's name).</param>
    /// <param name="estimatedSeconds">The job's estimated length, for the interval.</param>
    public void Begin(string label, double estimatedSeconds)
    {
        Error = null;
        LastPath = null;
        if (_camera.Get(_session.Host).IsRecording)
        {
            Error = "The camera is already recording; the time-lapse was not started.";
            _session.Note($"Time-lapse: {Error}");
            return;
        }
        try
        {
            var interval = IntervalFor(estimatedSeconds);
            var path = _camera.StartRecording(_session.Host, Path.GetFileNameWithoutExtension(label) + "_timelapse", interval);
            Recording = true;
            _wasPlaying = true;
            _session.Note($"Time-lapse: recording one frame every {interval.TotalSeconds:0.#} s to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            _session.Note($"Time-lapse: could not start recording: {ex.Message}");
        }
    }

    /// <summary>Stops the recording and keeps where it was written.</summary>
    public void End()
    {
        if (!Recording) return;
        Recording = false;
        LastPath = _camera.StopRecording(_session.Host);
        _session.Note($"Time-lapse: saved {LastPath}");
    }

    /// <summary>The job ended (or the connection dropped): the recording ends with it.</summary>
    private void OnSessionChanged()
    {
        var playing = _session.Status?.IsPlaying == true && _session.State == SessionState.Connected;
        if (_wasPlaying && !playing) End();
        _wasPlaying = playing;
    }
}
