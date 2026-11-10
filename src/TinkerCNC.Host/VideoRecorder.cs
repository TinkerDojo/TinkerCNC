using System.Threading.Channels;
using TinkerCNC.Video;
using TinkerCNC.Video.H264;
using TinkerCNC.Video.Mp4;

namespace TinkerCNC.Host;

/// <summary>
/// Records camera frames into an MP4 file (H.264) on its own task: the camera thread queues frames, this decodes,
/// encodes and writes them. Which frames are kept and how they are timed follows the <see cref="RecordingPlan"/>.
/// While it runs the file is "&lt;name&gt;.mp4.part" with a journal of its samples next to it, so a recording the
/// app did not get to finish is completed at the next start.
/// </summary>
public sealed class VideoRecorder
{
    public const string PartSuffix = ".part";

    /// <summary>Frames waiting to be encoded; when the encoder falls behind, new frames are dropped rather than queued.</summary>
    public const int QueueLength = 8;

    /// <summary>
    /// How often the part file is closed and opened again while recording. Windows updates the size a folder lists
    /// for a file only when the file is closed; without this the file would show its size only once finished.
    /// </summary>
    public static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(2);

    private readonly Channel<(byte[] Frame, DateTime Utc)> _queue = Channel.CreateBounded<(byte[], DateTime)>(
        new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly IFrameDecoder _decoder;
    private readonly ILogger _log;
    private readonly Task _loop;
    private FileStream? _file;
    private Mp4Writer? _writer;
    private Mp4VideoTrack? _track;
    private RecordingJournal? _journal;
    private H264Encoder? _encoder;
    private DateTime _firstUtc;
    private DateTime _openedUtc;
    private long _offered;
    private DateTime? _lastKeptUtc;
    private int _frames;
    private int _dropped;
    private bool _sizeMismatchLogged;
    private volatile Text? _error;

    public VideoRecorder(string path, RecordingPlan plan, IFrameDecoder decoder, ILogger log)
    {
        Path = path;
        Plan = plan;
        _decoder = decoder;
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    public RecordingPlan Plan { get; }

    /// <summary>The finished file.</summary>
    public string Path { get; }

    /// <summary>The file while recording.</summary>
    public string PartPath => Path + PartSuffix;

    public int FramesWritten => Volatile.Read(ref _frames);

    public int FramesDropped => Volatile.Read(ref _dropped);

    /// <summary>Why recording stopped early, or null.</summary>
    public Text? Error => _error;

    /// <summary>
    /// Offers a camera frame; called by one thread at a time. True when it was queued; false when the plan leaves
    /// it out, the queue is full (it is dropped) or the recording failed.
    /// </summary>
    public bool Add(byte[] frame, DateTime utc)
    {
        if (_error is not null) return false;
        if (!Plan.Selection.Keeps(_offered++, utc - _lastKeptUtc)) return false;
        _lastKeptUtc = utc;
        if (_queue.Writer.TryWrite((frame, utc))) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Takes no more frames, writes the queued ones and completes the file. Null when no frame was recorded.</summary>
    public async Task<string?> FinishAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        return Complete();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var (frame, utc) in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) Write(frame, utc);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Write(byte[] frame, DateTime utc)
    {
        if (_error is not null) return;
        var picture = _decoder.Decode(frame);
        if (picture is null) return;
        if (_encoder is null)
        {
            Open(picture, utc);
        }
        else if (picture.Width != _encoder.Settings.Width || picture.Height != _encoder.Settings.Height)
        {
            if (!_sizeMismatchLogged)
                _log.LogWarning("recording {Path}: a {W}x{H} frame does not match the recording's {RW}x{RH}; such frames are left out",
                    Path, picture.Width, picture.Height, _encoder.Settings.Width, _encoder.Settings.Height);
            _sizeMismatchLogged = true;
            return;
        }
        var timestamp = Plan.Timing.TimestampOf(_frames, utc - _firstUtc);
        var coded = _encoder!.Encode(picture);
        var sample = _writer!.AddSample(coded.Nal, timestamp, coded.IsKeyframe);
        _file!.Flush();                     // the sample is out of this process before the journal names it
        _journal!.Append(sample);
        Interlocked.Increment(ref _frames);
        if (utc - _openedUtc >= ReopenInterval) Reopen(utc);
    }

    private void Open(YuvPicture first, DateTime utc)
    {
        _firstUtc = utc;
        _openedUtc = utc;
        _encoder = new H264Encoder(new H264Settings(first.Width, first.Height));
        _track = new Mp4VideoTrack(first.Width, first.Height, Plan.Timing.Timescale, _encoder.Sps, _encoder.Pps);
        _file = OpenPart(FileMode.CreateNew);
        _writer = new Mp4Writer(_file, _track);
        _journal = RecordingJournal.Create(PartPath, _track);
    }

    /// <summary>Closes the part file, so its size shows in the folder, and carries on writing it.</summary>
    private void Reopen(DateTime utc)
    {
        _openedUtc = utc;
        var samples = _writer!.Samples;
        _file!.Dispose();
        _file = OpenPart(FileMode.Open);
        _writer = Mp4Writer.Resume(_file, _track!, samples);
    }

    private FileStream OpenPart(FileMode mode) => new(PartPath, mode, FileAccess.ReadWrite, FileShare.Read, 1 << 16);

    private string? Complete()
    {
        if (_writer is null || _file is null || _journal is null) return null;
        try
        {
            _writer.Finish();
            _file.Dispose();
            _journal.Dispose();
            File.Move(PartPath, Path);
            File.Delete(_journal.Path);
            if (FramesDropped > 0) _log.LogInformation("recording {Path}: {Dropped} frames dropped while encoding", Path, FramesDropped);
            return Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
            return null;
        }
    }

    private void Fail(Exception ex)
    {
        _error ??= Text.Of(ex);
        _log.LogWarning(ex, "recording {Path} failed", Path);
    }
}
