using System.Collections.Concurrent;
using TinkerCNC.GCode;
using TinkerCNC.History;
using TinkerCNC.Jobs;
using TinkerCNC.Protocol;
using TinkerCNC.Transfer;

namespace TinkerCNC.Host;

public enum JobPhase { Idle, Resolving, Downloading, Ready, NoFileReported, Failed }

/// <summary>A job as it ended: what ran, where it was and whether it got to its last operation.</summary>
/// <param name="Path">The file as the machine named it, when it was asked (a job started elsewhere); otherwise the file name.</param>
/// <param name="LastLine">The last line of the program the machine reported (in the file's numbering).</param>
/// <param name="EndedEarly">The last line reported lies before the last feed move of the program's last operation.</param>
/// <param name="Alarm">The machine was in alarm when the job ended.</param>
public sealed record JobRun(string? FileName, string? Path, GCodeAnalysis? Analysis, int? LastLine, bool EndedEarly, bool Alarm, DateTime EndedUtc)
{
    /// <summary>The operation the job was in when it ended early, if known.</summary>
    public ToolSegment? Interrupted => EndedEarly && Analysis is not null && LastLine is { } line ? Analysis.SegmentAt(line) : null;
}

/// <summary>
/// Follows the job running on one machine: when the status reports playback (<c>P</c> field) it asks which file is running (0xB7), downloads it
/// (cached by MD5) and analyses it. While the job runs, every tool operation is timed (<see cref="OperationRecorder"/>)
/// and stored in the <see cref="OperationHistory"/>; recorded times replace the estimate on the <see cref="Timeline"/>.
/// A job started here with probing also has its <see cref="JobPreparation"/> on the timeline, timed the same way.
/// </summary>
public sealed class JobTracker : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);
    private const int MaxBufferedSamples = 100_000;

    private readonly MachineSession _session;
    private readonly GCodeCache _cache;
    private readonly OperationHistory _history;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly List<MachineSample> _samples = new(); // since the job started, until the program is known
    private OperationRecorder? _recorder;
    private MachineLineMap? _lines;
    private ToolChangeLog? _toolChanges;
    private PreparationRecorder? _preparationRecorder;
    private PreparationRun? _preparationRun; // measured, not stored yet
    private JobTimeline? _timeline;          // the program alone
    private Shown? _shown;
    private readonly ConcurrentDictionary<string, OperationStatistics?> _preparationTimes = new();
    private Func<JobPreparation?>? _preview;
    private JobPreparation? _expected;       // of the job about to be started here
    private JobPreparation? _running;        // of the job running (or last run)
    private DateTime? _playingSinceUtc;
    private MachineSample? _lastSample;
    private int _resolving;
    private bool _wasPlaying;
    private bool _resolvedForCurrentJob;
    private DateTime _nextAttemptUtc;

    public JobTracker(MachineSession session, GCodeCache cache, OperationHistory history, ILogger log)
    {
        _session = session;
        _cache = cache;
        _history = history;
        _log = log;
        _session.Changed += OnSessionChanged;
    }

    public JobPhase Phase { get; private set; }
    public string? Error { get; private set; }
    public RunningFile? File { get; private set; }
    public bool LoadedLocally { get; private set; }
    public DownloadProgress? Download { get; private set; }

    /// <summary>
    /// The program with measured operations replacing the estimate, after the probing of the job (as chosen for a
    /// program about to be started, or as it was started); null until a program is known.
    /// </summary>
    public JobTimeline? Timeline
    {
        get
        {
            if (_timeline is not { } program) return null;
            var preparation = Preparation;
            var measured = preparation is null ? null : _preparationTimes.GetOrAdd(preparation.Fingerprint, PreparationTime);
            if (_shown is { } shown && shown.Program == program && shown.Fingerprint == preparation?.Fingerprint
                && shown.Estimate == preparation?.EstimatedSeconds && shown.Measured == measured)
                return shown.Result;
            var result = program.WithPreparation(preparation, measured);
            _shown = new Shown(program, preparation?.Fingerprint, preparation?.EstimatedSeconds, measured, result);
            return result;
        }
    }

    /// <summary>The timeline as last built, so renders don't rebuild it.</summary>
    private sealed record Shown(JobTimeline Program, string? Fingerprint, double? Estimate, OperationStatistics? Measured, JobTimeline Result);

    private JobPreparation? Preparation => LoadedLocally && !IsPlaying ? Preview() : _running;

    /// <summary>The probing of a job running: the line stays 0 until the program begins.</summary>
    public bool Preparing => IsPlaying && _lastSample is { Playing: true, Line: 0 };

    /// <summary>Where the running job is on the <see cref="Timeline"/>; null when nothing is playing.</summary>
    public TimelinePosition? Position
    {
        get
        {
            if (Timeline is not { } timeline || CurrentLine is not { } line) return null;
            return Preparing
                ? timeline.WhilePreparing((DateTime.UtcNow - (_playingSinceUtc ?? DateTime.UtcNow)).TotalSeconds, FeedOverridePercent)
                : timeline.At(line, FeedOverridePercent);
        }
    }

    /// <summary>The operation being timed right now.</summary>
    public ToolSegment? Measuring => _recorder?.Measuring;

    /// <summary>When each tool change happens (predicted or observed) and when the job ends.</summary>
    public ToolChangeSchedule? Schedule(DateTime nowUtc)
    {
        if (Timeline is not { } timeline) return null;
        Dictionary<int, DateTime> reached;
        lock (_gate) reached = new Dictionary<int, DateTime>(_toolChanges?.Reached ?? new Dictionary<int, DateTime>());
        return ToolChangeSchedule.Create(timeline, Position, nowUtc, reached);
    }

    public bool IsPlaying => _session.Status?.IsPlaying == true;

    /// <summary>The job that ended last, until the next one starts; null before any ended.</summary>
    public JobRun? LastRun { get; private set; }
    public JobProgress? Progress => _session.Status?.Job;
    public double FeedOverridePercent => _session.Status?.FeedOverridePercent ?? 100;

    /// <summary>Line of the file being executed (the machine's own numbering until the program is known).</summary>
    public int? CurrentLine => IsPlaying && _lastSample is { Playing: true } sample
        ? _timeline is { } t ? Math.Clamp(sample.InFileLines(t.Analysis.MachineLines).Line, 1, t.Analysis.LineCount) : sample.Line
        : null;

    public event Action? Changed;

    /// <summary>Queries and downloads the running file now, ignoring the retry delay.</summary>
    public Task RefreshAsync() => ResolveAsync();

    /// <summary>Shows a program that is not running (yet), e.g. one chosen to be started.</summary>
    /// <param name="preview">The probing it would be started with now (it follows the options as they change).</param>
    public void ShowProgram(string fileName, GCodeAnalysis analysis, Func<JobPreparation?>? preview = null)
    {
        File = new RunningFile(fileName, null);
        LoadedLocally = true;
        Error = null;
        _preview = preview;
        _running = null;
        UseProgram(analysis);
        SetPhase(JobPhase.Ready);
    }

    /// <summary>Stops showing a program that is not running; a running job stays as it is.</summary>
    public void ClearProgram()
    {
        if (IsPlaying) return;
        File = null;
        LoadedLocally = false;
        Error = null;
        _preview = null;
        _timeline = null;
        _shown = null;
        SetPhase(JobPhase.Idle);
    }

    /// <summary>The next job to start is started here with this probing (null: none, or the start failed).</summary>
    public void ExpectPreparation(JobPreparation? preparation) => _expected = preparation;

    private JobPreparation? Preview()
    {
        try
        {
            return _preview?.Invoke();
        }
        catch (InvalidOperationException)
        {
            return null; // the options don't fit (yet)
        }
    }

    private OperationStatistics? PreparationTime(string fingerprint)
    {
        try
        {
            return _history.GetStatistics([fingerprint], _session.Name).GetValueOrDefault(fingerprint);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read probing times from {Path}", _history.DatabasePath);
            return null;
        }
    }

    private void OnSessionChanged()
    {
        var playing = IsPlaying;
        if (playing && !_wasPlaying) OnJobStarted();
        if (!playing && _wasPlaying) OnJobEnded();
        _wasPlaying = playing;
        RecordSample();

        if (playing && !LoadedLocally && !_resolvedForCurrentJob && DateTime.UtcNow >= _nextAttemptUtc)
            _ = ResolveAsync();
        else
            Changed?.Invoke();
    }

    /// <summary>Playback started (or was already running when we connected): find out what is running.</summary>
    private void OnJobStarted()
    {
        _resolvedForCurrentJob = false;
        LoadedLocally = false;
        _nextAttemptUtc = DateTime.MinValue;
        _running = Interlocked.Exchange(ref _expected, null);
        _playingSinceUtc = null;
        lock (_gate)
        {
            _recorder = null;
            _toolChanges = null;
            _preparationRecorder = null;
            _samples.Clear();
            if (_lastSample is { Playing: false } start) _samples.Add(start); // marks the moment the job started
        }
    }

    /// <summary>Playback stopped: keep what ran and where it was, before the sample of the stopped machine replaces it.</summary>
    private void OnJobEnded()
    {
        var analysis = _timeline?.Analysis;
        int? line = _lastSample is { Playing: true } sample ? (_lines is { } map ? sample.InFileLines(map).Line : sample.Line) : null;
        var endedEarly = analysis?.Segments.LastOrDefault() is { } last && line is { } l && l < last.LastFeedLine;
        LastRun = new JobRun(File?.FileName, File?.Path, analysis, line, endedEarly, JobSuspension.HasState(_session.Status, "Alarm"), DateTime.UtcNow);
    }

    /// <summary>One sample per status report (other session events would only repeat the last status).</summary>
    private void RecordSample()
    {
        if (_session.StatusUtc is not { } utc || utc == _lastSample?.TimestampUtc) return;
        var sample = MachineSample.From(_session.Status, utc, _lastSample);
        if (sample.Playing && _playingSinceUtc is null) _playingSinceUtc = utc;
        _lastSample = sample;

        IReadOnlyList<OperationRun> completed;
        lock (_gate)
        {
            if (_recorder is null)
            {
                if (_samples.Count < MaxBufferedSamples) _samples.Add(sample);
                return;
            }
            completed = Observe(sample);
        }
        Save(completed);
    }

    private async Task ResolveAsync()
    {
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            LoadedLocally = false;
            Error = null;
            SetPhase(JobPhase.Resolving);

            var file = await _session.QueryRunningFileAsync();
            if (file is null)
            {
                _nextAttemptUtc = DateTime.UtcNow + RetryDelay;
                SetPhase(JobPhase.NoFileReported);
                return;
            }

            File = file;
            _session.Note($"Machine is running {file.Path} (reported checksum: {file.Md5 ?? "none"})");
            UseProgram(_cache.Find(file.Md5) ?? await DownloadAsync(file));
            _resolvedForCurrentJob = true;
            SetPhase(JobPhase.Ready);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not resolve the running job on {Address}", _session.Address);
            Error = ex.Message;
            _nextAttemptUtc = DateTime.UtcNow + RetryDelay;
            SetPhase(JobPhase.Failed);
        }
        finally
        {
            Download = null;
            Interlocked.Exchange(ref _resolving, 0);
        }
    }

    /// <summary>
    /// Starts a download; the machine announces the file's MD5 first, so an identical cached copy is used without
    /// transferring the data.
    /// </summary>
    private async Task<GCodeAnalysis> DownloadAsync(RunningFile file)
    {
        SetPhase(JobPhase.Downloading);
        var progress = new Progress<DownloadProgress>(p => { Download = p; Changed?.Invoke(); });
        var result = await _session.DownloadAsync(file.Path, progress, _cache);
        return _cache.Add(file.FileName, result.Content);
    }

    /// <summary>Starts timing operations of <paramref name="analysis"/>, replaying what happened since the job started.</summary>
    private void UseProgram(GCodeAnalysis analysis)
    {
        var timeline = BuildTimeline(analysis);
        var completed = new List<OperationRun>();
        lock (_gate)
        {
            _recorder = new OperationRecorder(analysis);
            _preparationRecorder = _running is { } preparation && !LoadedLocally ? new PreparationRecorder(preparation, analysis) : null;
            _lines = analysis.MachineLines;
            _toolChanges = new ToolChangeLog(timeline);
            foreach (var sample in _samples) completed.AddRange(Observe(sample));
            _samples.Clear();
        }
        _timeline = timeline;
        Save(completed);
    }

    /// <summary>Feeds one sample to the timers and the tool-change log (caller holds the lock).</summary>
    private IReadOnlyList<OperationRun> Observe(MachineSample sample)
    {
        var inFile = sample.InFileLines(_lines!);
        _toolChanges?.Observe(inFile);
        if (_preparationRecorder?.Observe(inFile) is { } preparation) _preparationRun = preparation;
        return _recorder!.Observe(inFile);
    }

    private void Save(IReadOnlyList<OperationRun> runs)
    {
        var preparation = Interlocked.Exchange(ref _preparationRun, null);
        if ((runs.Count == 0 && preparation is null) || _timeline is not { } timeline) return;
        try
        {
            if (preparation is { } p) SavePreparation(p);
            foreach (var run in runs)
            {
                _history.Record(new OperationRunRecord(
                    run.Segment.Fingerprint, _session.Name, File?.FileName, run.Segment.ToolNumber,
                    run.Segment.EndLine - run.Segment.CuttingStartLine + 1, run.Segment.CuttingSeconds,
                    run.ActiveSeconds, run.PausedSeconds, run.AverageFeedOverridePercent, DateTime.UtcNow));
                _session.Note($"Recorded {run.Segment.Label}: {TimeFormat.Duration(run.ActiveSeconds)} " +
                              $"(estimate {TimeFormat.Duration(run.Segment.CuttingSeconds)})");
            }
            _timeline = BuildTimeline(timeline.Analysis);
            _toolChanges?.Use(_timeline);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not store operation times in {Path}", _history.DatabasePath);
        }
        Changed?.Invoke();
    }

    private void SavePreparation(PreparationRun run)
    {
        var preparation = run.Preparation;
        _history.Record(new OperationRunRecord(
            preparation.Fingerprint, _session.Name, File?.FileName, null, 0, preparation.EstimatedSeconds,
            run.ActiveSeconds, run.PausedSeconds, run.AverageFeedOverridePercent, DateTime.UtcNow));
        _preparationTimes.TryRemove(preparation.Fingerprint, out _);
        _session.Note($"Recorded {JobPreparation.Label.ToLowerInvariant()}: {TimeFormat.Duration(run.ActiveSeconds)} " +
                      $"(estimate {TimeFormat.Duration(preparation.EstimatedSeconds)})");
    }

    private JobTimeline BuildTimeline(GCodeAnalysis analysis)
    {
        try
        {
            return JobTimeline.Create(analysis, _history.GetStatistics(analysis.Segments.Select(s => s.Fingerprint), _session.Name));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read operation times from {Path}", _history.DatabasePath);
            return JobTimeline.Estimated(analysis);
        }
    }

    private void SetPhase(JobPhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}

/// <summary>
/// One <see cref="JobTracker"/> per machine session, created with the session so that operations are timed
/// whether or not anyone has the dashboard open.
/// </summary>
public sealed class JobTrackers : IDisposable
{
    private readonly ConcurrentDictionary<string, JobTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly GCodeCache _cache;
    private readonly OperationHistory _history;
    private readonly ILogger<JobTracker> _log;

    public JobTrackers(MachineSessionManager sessions, GCodeCache cache, OperationHistory history, ILogger<JobTracker> log)
    {
        _cache = cache;
        _history = history;
        _log = log;
        sessions.SessionCreated += s => Get(s);
    }

    public JobTracker Get(MachineSession session) =>
        _trackers.GetOrAdd(session.Address, _ => new JobTracker(session, _cache, _history, _log));

    public void Dispose()
    {
        foreach (var t in _trackers.Values) t.Dispose();
    }
}
