using TinkerCNC.Jobs;
using TinkerCNC.Video;

namespace TinkerCNC.Host;

/// <summary>
/// Records a time-lapse of a job started here (<see cref="JobStartOptions.Timelapse"/>) with the machine's camera:
/// from the moment the machine plays until it stops playing, one frame every <see cref="Interval"/> into an MP4
/// file in the recordings folder that plays <see cref="PlaybackFramesPerSecond"/> frames a second
/// (<see cref="CameraService.StartRecording"/>). The camera is started if it is off. A recording that was already
/// running is left alone.
/// </summary>
public sealed class JobTimelapse
{
    /// <summary>Time between the frames of a time-lapse.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How fast the time-lapse plays: with one frame a second recorded, 30 times faster than the job.</summary>
    public const int PlaybackFramesPerSecond = 30;

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

    /// <summary>Where the last time-lapse was written, once the job ended and the file was completed.</summary>
    public string? LastPath { get; private set; }

    /// <summary>Why the last time-lapse could not be recorded, or null.</summary>
    public Text? Error { get; private set; }

    /// <summary>How much faster than the job the time-lapse plays.</summary>
    public static double SpeedUp => PlaybackFramesPerSecond * Interval.TotalSeconds;

    /// <summary>Starts recording the job that is playing now.</summary>
    /// <param name="label">Start of the file name (the program's name).</param>
    public void Begin(string label)
    {
        Error = null;
        LastPath = null;
        if (_camera.Get(_session.Host).IsRecording)
        {
            Error = HostTexts.Of("Timelapse.CameraBusy");
            _session.Note($"Time-lapse: {Error}");
            return;
        }
        try
        {
            var plan = RecordingPlan.Timelapse(Interval, PlaybackFramesPerSecond);
            var path = _camera.StartRecording(_session.Host, Path.GetFileNameWithoutExtension(label) + "_timelapse", plan);
            Recording = true;
            _wasPlaying = true;
            _session.Note($"Time-lapse: recording one frame every {Interval.TotalSeconds:0.#} s to {path}; it plays {SpeedUp:0}× faster than the job");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Text.Of(ex);
            _session.Note($"Time-lapse: could not start recording: {ex.Message}");
        }
    }

    /// <summary>Stops the recording; the file is completed in the background and its path kept.</summary>
    public void End()
    {
        if (!Recording) return;
        Recording = false;
        _ = FinishAsync();
    }

    private async Task FinishAsync()
    {
        try
        {
            LastPath = await _camera.StopRecordingAsync(_session.Host).ConfigureAwait(false);
            _session.Note(LastPath is null ? "Time-lapse: no frames were recorded" : $"Time-lapse: saved {LastPath}");
        }
        catch (Exception ex)
        {
            Error = Text.Of(ex);
            _session.Note($"Time-lapse: could not finish the recording: {ex.Message}");
        }
    }

    /// <summary>The job ended (or the connection dropped): the recording ends with it.</summary>
    private void OnSessionChanged()
    {
        var playing = _session.Status?.IsPlaying == true && _session.State == SessionState.Connected;
        if (_wasPlaying && !playing) End();
        _wasPlaying = playing;
    }
}
