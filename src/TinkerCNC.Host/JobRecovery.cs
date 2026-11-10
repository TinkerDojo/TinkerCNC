using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using TinkerCNC.GCode;
using TinkerCNC.Recovery;

namespace TinkerCNC.Host;

/// <summary>
/// Keeps where a machine's job is (<see cref="JobSnapshot"/>), so it can be resumed at that line after an emergency
/// stop, an alarm or the machine switched off, also after the app was closed (<see cref="JobControl.LoadResume"/>):
/// every <see cref="Interval"/> while the job runs, and at once with the last line seen when it ends early. A job
/// that runs to its end leaves none. The program itself is kept in the program cache (by MD5).
/// </summary>
public sealed class JobRecovery : IDisposable
{
    /// <summary>How often the line is stored while a job runs: at most this much of a job has to run again.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly MachineSession _session;
    private readonly JobTracker _tracker;
    private readonly JobControl _control;
    private readonly GCodeCache _cache;
    private readonly JobSnapshotRepository _repository;
    private GCodeAnalysis? _program;
    private string? _md5;
    private DateTime _savedUtc;
    private DateTime _startedUtc;
    private bool _wasPlaying;
    private bool _leveled;
    private int? _lastLine; // the last line seen running, for an end the tracker does not see (the connection lost)

    public JobRecovery(MachineSession session, JobTracker tracker, JobControl control, GCodeCache cache, JobSnapshotRepository repository)
    {
        _session = session;
        _tracker = tracker; // subscribed to the session before this, so its view of a job that ended is up to date here
        _control = control;
        _cache = cache;
        _repository = repository;
        Snapshot = Load();
        _session.Changed += OnSessionChanged;
    }

    public event Action? Changed;

    /// <summary>Where the last job that did not run to its end stopped; null when there is none.</summary>
    public JobSnapshot? Snapshot { get; private set; }

    /// <summary>The program of <see cref="Snapshot"/>, from the program cache; null when it is gone.</summary>
    public GCodeAnalysis? SnapshotProgram => Snapshot is { } snapshot ? _cache.Find(snapshot.Md5) : null;

    /// <summary>Forgets the stopped job: it will not be resumed.</summary>
    public void Discard()
    {
        Snapshot = null;
        Store(repository => repository.Delete(_session.Address));
        Changed?.Invoke();
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;

    // A lost connection ends the job here too: the last status (still "playing") stays with the session.
    private void OnSessionChanged()
    {
        var playing = _session.State == SessionState.Connected && _tracker.IsPlaying;
        if (playing && !_wasPlaying) OnJobStarted();
        if (playing && !_tracker.Preparing && _tracker.CurrentLine is > 0 and var line) _lastLine = line;
        if (!playing && _wasPlaying) OnJobEnded();
        _wasPlaying = playing;
        if (playing && _lastLine is not null && DateTime.UtcNow - _savedUtc >= Interval) Save(_lastLine);
    }

    /// <summary>A job started here runs with the options of the start (the start is still waiting for it to play).</summary>
    private void OnJobStarted()
    {
        _leveled = _control.Phase == JobControlPhase.Starting && _control.Options.Leveling;
        _savedUtc = DateTime.MinValue;
        _startedUtc = DateTime.UtcNow;
        _lastLine = null; // probing first: no line of the program has run yet
    }

    /// <summary>
    /// Ran to its end: nothing to resume. Anything else (an alarm, Stop, the connection lost): keep the last line it
    /// was seen at. The tracker's account of the end counts only when it is about this job.
    /// </summary>
    private void OnJobEnded()
    {
        var run = _tracker.LastRun is { EndedUtc: var ended } r && ended >= _startedUtc ? r : null;
        if (_session.State == SessionState.Connected && run is { EndedEarly: false, Alarm: false, LastLine: not null })
        {
            if (Snapshot is not null) Discard();
            return;
        }
        Save(run?.LastLine is > 0 ? run.LastLine : _lastLine);
    }

    /// <summary>Stores the line of the running program; nothing while the program or a line of it is not known yet.</summary>
    private void Save(int? line)
    {
        if (line is not > 0 || _tracker.Timeline?.Analysis is not { } program || Md5Of(program) is not { } md5) return;
        Snapshot = new JobSnapshot(_session.Address, _tracker.File?.FileName ?? "program.nc", md5, line.Value, program.LineCount,
            _session.Status?.WorkZero, _leveled, DateTime.UtcNow);
        _savedUtc = Snapshot.RecordedUtc;
        Store(repository => repository.Save(Snapshot));
        Changed?.Invoke();
    }

    /// <summary>The program as it ran, put into the program cache once per program so a resume finds it later.</summary>
    private string? Md5Of(GCodeAnalysis program)
    {
        if (ReferenceEquals(program, _program)) return _md5;
        var content = Encoding.UTF8.GetBytes(string.Join('\n', program.Lines)); // the lines end with an empty one when the file ended with a line break
        try
        {
            _cache.Add(_tracker.File?.FileName ?? "program.nc", content);
            (_program, _md5) = (program, GCodeCache.Md5Of(content));
        }
        catch (IOException ex)
        {
            _session.Note($"Recovery: could not keep a copy of the program: {ex.Message}");
            (_program, _md5) = (program, null);
        }
        return _md5;
    }

    private JobSnapshot? Load()
    {
        try { return _repository.Get(_session.Address); }
        catch (SqliteException ex)
        {
            _session.Note($"Recovery: could not read where the last job stopped: {ex.Message}");
            return null;
        }
    }

    /// <summary>A snapshot that can't be written only costs the resume: the job goes on.</summary>
    private void Store(Action<JobSnapshotRepository> write)
    {
        try { write(_repository); }
        catch (SqliteException ex) { _session.Note($"Recovery: could not store where the job is: {ex.Message}"); }
    }
}

/// <summary>One <see cref="JobRecovery"/> per machine session, created with the session.</summary>
public sealed class JobRecoveries : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<JobRecovery>> _recoveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly JobTrackers _trackers;
    private readonly JobControls _controls;
    private readonly GCodeCache _cache;
    private readonly JobSnapshotRepository _repository;

    public JobRecoveries(MachineSessionManager sessions, JobTrackers trackers, JobControls controls, GCodeCache cache, JobSnapshotRepository repository)
    {
        (_trackers, _controls, _cache, _repository) = (trackers, controls, cache, repository);
        sessions.SessionCreated += s => Get(s);
    }

    // Lazy: a second instance would also watch the session.
    public JobRecovery Get(MachineSession session) => _recoveries.GetOrAdd(session.Address, _ => new Lazy<JobRecovery>(() =>
        new JobRecovery(session, _trackers.Get(session), _controls.Get(session), _cache, _repository))).Value;

    public void Dispose()
    {
        foreach (var recovery in _recoveries.Values.Where(r => r.IsValueCreated)) recovery.Value.Dispose();
    }
}
