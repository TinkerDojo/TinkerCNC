using System.Collections.Concurrent;
using TinkerCNC.Host.Notifications;
using TinkerCNC.Jobs;
using TinkerCNC.Programs;
using TinkerCNC.Queue;

namespace TinkerCNC.Host;

/// <summary>The queue can't do that right now.</summary>
public sealed class QueueException(Text text) : LocalizedException(text);

/// <summary>A queue entry as shown: with the names its ids stand for.</summary>
/// <param name="SetupName">Null for a job started from a file that is in no setup.</param>
public sealed record QueueItem(QueueEntry Entry, string? SetupName, string FileName, bool FileMissing)
{
    public string Label => SetupName is null ? FileName : $"{SetupName} · {FileName}";

    /// <summary>
    /// Why its last job failed, for people: as the queue raised it while the app runs, otherwise the English words
    /// stored with the entry (<see cref="QueueEntry.Error"/>).
    /// </summary>
    public Text? Error { get; init; } = Entry.Error is { } stored ? Text.Plain(stored) : null;
}

/// <summary>
/// The job queue of one machine: its entries (<see cref="QueueEntry"/>) run one after the other, each with the options
/// of its setup as they are when it starts, and each waiting for the go before anything moves (<see cref="JobGate"/>):
/// a press of the machine's button, or Resume here. A job started on the Job card is the queue's first entry, with the
/// options set there, and starts the queue (<see cref="StartJobAsync"/>): it runs at once, and more runs of it
/// (<see cref="SetRepeat"/>, also while it runs) and the entries after it follow. A failure (an alarm, Stop, a refused start, the
/// connection lost, a job that ended before its last operation) stops the queue with the entry marked failed; a
/// retry can begin at the tool operation that was interrupted (<see cref="JobControl.StartFrom"/>). The entries and
/// the copies queued for one run only live in the database and the queued files folder, so a queue survives a
/// restart of the app, stopped. Started while a job runs that the queue did not start (one started on the Job card or
/// elsewhere), the queue takes that job as its first one: it goes on once the job has finished, and stops when it fails.
/// </summary>
public sealed class JobQueue : IDisposable
{
    public const int MaxRepeat = 999;

    private readonly MachineSession _session;
    private readonly JobControl _control;
    private readonly JobTracker _tracker;
    private readonly QueueRepository _repository;
    private readonly SetupLibrary _library;
    private readonly MachineNotifications _notifications;
    private readonly string _directory;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<long, Text> _failures = new(); // why entries failed while the app runs
    private IReadOnlyList<QueueItem>? _items;
    private bool _wasPlaying;
    private bool _gateRaised;
    private bool _afterRunningJob;
    private DateTime _startedUtc;

    public JobQueue(MachineSession session, JobControl control, JobTracker tracker, QueueRepository repository, SetupLibrary library,
        MachineNotifications notifications, string directory)
    {
        _session = session;
        _control = control;
        _tracker = tracker;
        _repository = repository;
        _library = library;
        _notifications = notifications;
        _directory = directory;
        Directory.CreateDirectory(directory);
        _session.Changed += OnSessionChanged;
        _library.Changed += Reload;
    }

    /// <summary>Raised after any change, from the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>Whether entries start one after the other right now.</summary>
    public bool Running { get; private set; }

    /// <summary>The entry whose job is on the machine or being started.</summary>
    public QueueEntry? Current { get; private set; }

    /// <summary>Why the queue stopped, until it is started or cleared again.</summary>
    public Text? Error { get; private set; }

    /// <summary>Every entry, done ones included, in queue order.</summary>
    public IReadOnlyList<QueueItem> Items => _items ??= Build();

    /// <summary>The entries still listed: everything but the done ones.</summary>
    public IReadOnlyList<QueueItem> Listed => Items.Where(i => i.Entry.State != QueueEntryState.Done).ToList();

    /// <summary>Runs that finished, over every entry.</summary>
    public int JobsDone => Items.Sum(i => i.Entry.Done);

    /// <summary>Runs in the queue altogether, finished ones included.</summary>
    public int JobsTotal => Items.Sum(i => i.Entry.Repeat);

    /// <summary>Whether the queue waits for a job it did not start to finish, and goes on after it.</summary>
    public bool AfterRunningJob => Running && Current is null && _afterRunningJob;

    /// <summary>Whether a queued job is held right after its start, waiting for the go.</summary>
    public bool WaitingForGo => Current is not null && JobGate.IsHeld(_session.Status) && _tracker.Preparing;

    /// <summary>The item whose job is on the machine.</summary>
    public QueueItem? CurrentItem => Current is { } current ? Items.FirstOrDefault(i => i.Entry.Id == current.Id) : null;

    /// <summary>Queues one of a setup's files.</summary>
    /// <exception cref="QueueException">The setup can't be queued (its work origin is the head position).</exception>
    public QueueEntry AddSetupFile(long fileId, int repeat)
    {
        var file = _library.GetFile(fileId);
        var setup = Queueable(_library.Get(file.SetupId));
        if (file.IsDeleted) throw new QueueException(HostTexts.Of("Queue.FileDeleted", file.OriginalFileName));
        return Insert(setup.Id, file.Id, null, null, null, repeat);
    }

    /// <summary>Queues a copy of a file for its runs only, with a setup's options.</summary>
    public QueueEntry AddCopy(long setupId, string fileName, byte[] content, int repeat)
    {
        var setup = Queueable(_library.Get(setupId));
        return InsertCopy(setup.Id, fileName, content, repeat);
    }

    /// <summary>
    /// Starts the job chosen on the Job card at once, as the queue's first entry, and the queue with it. It runs with the
    /// options as they are on the card and without waiting for the go (whoever pressed Start is at the machine); further
    /// runs of it (<see cref="SetRepeat"/>) and the entries after it follow, each waiting for the go. A start that fails
    /// leaves no entry behind; the Job card shows why (<see cref="JobControl.LastError"/>).
    /// </summary>
    /// <exception cref="QueueException">No program is chosen, or the machine can't start one right now.</exception>
    public async Task StartJobAsync()
    {
        if (_control.WhyNotStart is { } reason) throw new QueueException(reason);
        QueueEntry entry;
        lock (_lock)
        {
            if (Current is not null) throw new QueueException(HostTexts.Of("Job.Busy"));
            entry = InsertCardJob();
            Current = entry;
            Running = true;
            Error = null;
            _gateRaised = false;
            _afterRunningJob = false;
        }
        SetState(entry, QueueEntryState.Running, 0, null, _control.StartSegment?.Index);
        _startedUtc = DateTime.UtcNow;
        _control.WaitForGo = false;
        await _control.StartAsync();
        if (_control.LastError is not null)
        {
            Discard(entry);
            return;
        }
        lock (_lock) _wasPlaying = true;
        _session.Note($"Queue: {Label(entry)} started from the Job card (job {JobsDone + 1} of {JobsTotal}).");
    }

    /// <summary>Takes an entry out; a queued copy's file goes with it. The running entry stays.</summary>
    public void Remove(long id)
    {
        var entry = NotRunning(id);
        DeleteCopy(entry);
        _repository.Remove(id);
        Reload();
    }

    /// <summary>Moves an entry up (negative) or down among the listed ones.</summary>
    public void Move(long id, int delta)
    {
        var listed = Listed.Select(i => i.Entry).ToList();
        var index = listed.FindIndex(e => e.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= listed.Count) return;
        var other = listed[target];
        _repository.UpdatePosition(listed[index].Id, other.Position);
        _repository.UpdatePosition(other.Id, listed[index].Position);
        Reload();
    }

    /// <summary>
    /// How often an entry runs, done runs included; also for the one on the machine, whose run counts (so it can't go
    /// below the runs done plus that one).
    /// </summary>
    /// <exception cref="QueueException">More runs of a job whose work origin is the head position (only the run started by
    /// hand can take it from there).</exception>
    public void SetRepeat(long id, int repeat)
    {
        var entry = Get(id);
        var least = Math.Max(1, entry.Done + (entry.Id == Current?.Id ? 1 : 0));
        var wanted = Math.Clamp(repeat, least, MaxRepeat);
        if (wanted > least && entry.Options?.WorkOrigin == WorkOriginChoice.HeadPosition)
            throw new QueueException(HostTexts.Of("Queue.HeadPositionRepeat"));
        _repository.UpdateRepeat(entry.Id, wanted);
        lock (_lock)
        {
            if (Current?.Id == entry.Id) Current = Current with { Repeat = wanted };
        }
        Reload();
    }

    /// <summary>A failed entry gets its next run, from the whole file or from a tool operation (its index).</summary>
    public void Retry(long id, int? fromSegment)
    {
        var entry = Get(id);
        if (entry.State != QueueEntryState.Failed) throw new QueueException(HostTexts.Of("Queue.OnlyFailedRetried"));
        _repository.UpdateState(id, QueueEntryState.Pending, entry.Done, null, fromSegment is > 0 ? fromSegment : null);
        Reload();
    }

    /// <summary>A queued copy becomes one of its setup's files, so it stays after the runs.</summary>
    public void KeepInSetup(long id)
    {
        var entry = Get(id);
        if (!entry.IsQueuedCopy) throw new QueueException(HostTexts.Of("Queue.AlreadyInSetup"));
        var setupId = entry.SetupId ?? throw new QueueException(HostTexts.Of("Queue.NoSetup"));
        var path = CopyPath(entry) ?? throw new QueueException(HostTexts.Of("Queue.CopyGone"));
        var file = _library.AddFile(setupId, entry.OriginalFileName ?? "program.nc", File.ReadAllBytes(path));
        _repository.UseSetupFile(id, file.Id);
        File.Delete(path);
        Reload();
    }

    /// <summary>Takes every entry out but the running one, done ones included, and forgets why the queue stopped.</summary>
    public void Clear()
    {
        foreach (var item in Items.Where(i => i.Entry.Id != Current?.Id))
        {
            DeleteCopy(item.Entry);
            _repository.Remove(item.Entry.Id);
        }
        Error = null;
        Reload();
    }

    /// <summary>
    /// Starts the first pending entry and goes on from there; while the machine runs a job, the queue begins once that
    /// job has finished (<see cref="AfterRunningJob"/>).
    /// </summary>
    /// <exception cref="QueueException">The machine can't start a job right now.</exception>
    public void Start()
    {
        bool afterRunningJob;
        lock (_lock)
        {
            if (Running) return;
            if (_session.State != SessionState.Connected) throw new QueueException(HostTexts.Of("Machine.NotConnected"));
            afterRunningJob = Current is null && _session.Status?.IsPlaying == true;
            if (Current is null && !afterRunningJob && !JobControl.CanStartIn(_session.Status))
                throw new QueueException(HostTexts.Of("Queue.NotReady", MachineSession.StateOf(_session.Status)));
            Running = true;
            Error = null;
            _afterRunningJob = afterRunningJob;
            _wasPlaying = afterRunningJob;
            _startedUtc = DateTime.UtcNow;
        }
        Changed?.Invoke();
        if (afterRunningJob) _session.Note("Queue: starts once the running job has finished.");
        else _ = Task.Run(RunNextAsync);
    }

    /// <summary>
    /// Nothing further starts; a job the queue started is stopped too (its entry fails as "stopped"), a job it only
    /// waits for goes on.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            Running = false;
            _afterRunningJob = false;
        }
        Changed?.Invoke();
        if (Current is not null && _session.Status?.IsPlaying == true) await _control.StopAsync();
    }

    /// <summary>The go for a job that waits for it.</summary>
    public Task ResumeAsync() => _control.ContinueAsync();

    /// <summary>The tool operations of an entry's file, for a retry that begins at one of them.</summary>
    public IReadOnlyList<GCode.ToolSegment> OperationsOf(long id)
    {
        var entry = Get(id);
        try
        {
            var content = entry.FileId is { } fileId ? _library.ReadFile(_library.GetFile(fileId))
                : CopyPath(entry) is { } path ? File.ReadAllBytes(path) : null;
            return content is null ? [] : GCodeCache.Analyze(System.Text.Encoding.UTF8.GetString(content)).Segments;
        }
        catch (SetupException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _library.Changed -= Reload;
    }

    private async Task RunNextAsync()
    {
        QueueEntry? entry;
        lock (_lock)
        {
            if (!Running || Current is not null) return;
            entry = NextPending();
            if (entry is null)
            {
                Running = false;
                _session.Note("Queue finished.");
                _notifications.Raise(new QueueNotification(HostTexts.Of("Queue.Finished.Title"), HostTexts.Of("Queue.Finished.Message", JobsDone),
                    "info", "check"));
                Changed?.Invoke();
                return;
            }
            Current = entry;
            _gateRaised = false;
            _afterRunningJob = false;
        }
        SetState(entry, QueueEntryState.Running, entry.Done, null, entry.RestartSegment);
        try
        {
            var (setup, file, name, content, options) = Resolve(entry);
            _control.LoadQueued(setup, file, name, content, options, entry.RestartSegment);
            _control.WaitForGo = true;
            _startedUtc = DateTime.UtcNow;
            await _control.StartAsync();
            if (_control.LastError is { } error)
            {
                Fail(entry, error);
                return;
            }
            lock (_lock) _wasPlaying = true;
            _session.Note($"Queue: {Label(entry)} started (job {JobsDone + 1} of {JobsTotal}).");
        }
        catch (Exception ex) when (ex is SetupException or QueueException or InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Fail(entry, Text.Of(ex));
        }
        finally
        {
            _control.WaitForGo = false;
        }
    }

    /// <summary>The first listed entry with runs left; entries with none are marked done on the way.</summary>
    private QueueEntry? NextPending()
    {
        foreach (var entry in Items.Select(i => i.Entry).Where(e => e.State == QueueEntryState.Pending))
        {
            if (entry.Remaining > 0) return entry;
            DeleteCopy(entry);
            SetState(entry, QueueEntryState.Done, entry.Done, null, null);
        }
        return null;
    }

    private void OnSessionChanged()
    {
        QueueEntry? ended = null;
        var awaitedJobEnded = false;
        lock (_lock)
        {
            var connected = _session.State == SessionState.Connected;
            var playing = connected && _session.Status?.IsPlaying == true;
            var wasPlaying = _wasPlaying;
            _wasPlaying = playing;
            if (Current is not { } current)
            {
                if (!Running) return;
                if (!connected)
                {
                    Running = false;
                    _afterRunningJob = false;
                    Error = HostTexts.Of("Queue.ConnectionLost");
                }
                else if (_afterRunningJob && wasPlaying && !playing)
                {
                    awaitedJobEnded = true;
                }
                else
                {
                    return;
                }
            }
            else if (wasPlaying && !playing)
            {
                ended = current;
            }
            else if (WaitingForGo && !_gateRaised)
            {
                _gateRaised = true;
                _notifications.Raise(new QueueGateNotification(HostTexts.Of("Queue.GateJob", Label(current), JobsDone + 1, JobsTotal)));
            }
            else
            {
                return;
            }
        }
        if (ended is { } entry)
        {
            if (EndReason() is { } reason) Fail(entry, reason);
            else Succeed(entry);
        }
        if (awaitedJobEnded) GoOnAfterRunningJob();
        Changed?.Invoke();
    }

    /// <summary>The job the queue waited for has ended: the queue begins, or stops when that job did not finish.</summary>
    private void GoOnAfterRunningJob()
    {
        var reason = EndReason();
        lock (_lock)
        {
            _afterRunningJob = false;
            if (reason is not null)
            {
                Running = false;
                Error = HostTexts.Of("Queue.BeforeFailed", reason);
            }
        }
        if (reason is null)
        {
            _ = Task.Run(RunNextAsync); // off the session's thread: the start sends commands and waits
            return;
        }
        _session.Note($"Queue stopped: the job before it did not finish: {reason}");
        _notifications.Raise(new QueueNotification(HostTexts.Of("Queue.Stopped.Title"), HostTexts.Of("Queue.BeforeFailed.Message", reason),
            "alarm", "alert"));
    }

    /// <summary>Why the job that just ended did not finish, or null when it did.</summary>
    private Text? EndReason()
    {
        if (_session.State != SessionState.Connected) return HostTexts.Of("Queue.ConnectionLost");
        if (JobSuspension.HasState(_session.Status, "Alarm"))
            return HostTexts.Of("Queue.Alarm", _session.LastAlarm is { } alarm ? Text.Plain(alarm) : HostTexts.Of("Queue.NoAlarmMessage"));
        if (_control.StopRequestedUtc > _startedUtc) return HostTexts.Of("Queue.Stopped");
        if (_tracker.LastRun is { EndedEarly: true } run && run.EndedUtc >= _startedUtc)
            return run.Interrupted is { } at ? HostTexts.Of("Queue.EndedEarlyAt", at.DisplayLabel) : HostTexts.Of("Queue.EndedEarly");
        return null;
    }

    private void Succeed(QueueEntry ended)
    {
        var entry = _repository.Get(ended.Id) ?? ended; // its repeat count may have changed while it ran
        var done = entry.Done + 1;
        var finished = done >= entry.Repeat;
        if (finished) DeleteCopy(entry);
        SetState(entry, finished ? QueueEntryState.Done : QueueEntryState.Pending, done, null, null);
        _session.Note($"Queue: {Label(entry)} finished ({done} of {entry.Repeat}).");
        lock (_lock) Current = null;
        Reload();
        _ = Task.Run(RunNextAsync); // off the session's thread: the next start sends commands and waits
    }

    /// <summary>The entry failed: the reason is stored in English with it and kept as a text for the dashboards.</summary>
    private void Fail(QueueEntry entry, Text reason)
    {
        var interrupted = _tracker.LastRun is { EndedEarly: true, EndedUtc: var at } run && at >= _startedUtc ? run.Interrupted?.Index : null;
        _failures[entry.Id] = reason;
        SetState(entry, QueueEntryState.Failed, entry.Done, reason.ToString(), interrupted is > 0 ? interrupted : null);
        var label = Label(entry);
        lock (_lock)
        {
            Current = null;
            Running = false;
            Error = HostTexts.Of("Queue.EntryFailed", label, reason);
        }
        _session.Note($"Queue stopped: {label}: {reason}");
        _notifications.Raise(new QueueNotification(HostTexts.Of("Queue.Stopped.Title"), HostTexts.Of("Queue.EntryFailed.Message", label, reason),
            "alarm", "alert"));
        Reload();
    }

    /// <summary>What a run of the entry needs: its setup (if any), file, and the options it runs with.</summary>
    private (Setup? Setup, SetupFile? File, string Name, byte[] Content, JobStartOptions Options) Resolve(QueueEntry entry)
    {
        var (setup, options) = SetupAndOptions(entry);
        if (entry.FileId is { } fileId)
        {
            var file = _library.FindFile(fileId);
            if (file is null || file.IsDeleted) throw new QueueException(HostTexts.Of("Queue.FileGone"));
            return (setup, file, file.OriginalFileName, _library.ReadFile(file), options);
        }
        var path = CopyPath(entry) ?? throw new QueueException(HostTexts.Of("Queue.CopyMissing"));
        return (setup, null, entry.OriginalFileName ?? "program.nc", File.ReadAllBytes(path), options);
    }

    /// <summary>
    /// The entry's own options (a job from the Job card; its setup, if still there, only names it), or its setup's as
    /// they are now. A later run can't set the work origin at the head position: nobody placed the head for it.
    /// </summary>
    private (Setup? Setup, JobStartOptions Options) SetupAndOptions(QueueEntry entry)
    {
        var setup = entry.SetupId is { } setupId ? _library.Find(setupId) : null;
        if (entry.Options is not { } own)
        {
            var queueable = Queueable(setup ?? throw new QueueException(HostTexts.Of("Queue.SetupGone")));
            return (queueable, queueable.Options);
        }
        if (own.WorkOrigin == WorkOriginChoice.HeadPosition) throw new QueueException(HostTexts.Of("Queue.HeadPositionRepeat"));
        return (setup is { IsDeleted: false } ? setup : null, own);
    }

    private static Setup Queueable(Setup setup)
    {
        if (setup.IsDeleted) throw new QueueException(HostTexts.Of("Setup.Deleted", setup.Name));
        if (setup.Options.WorkOrigin == WorkOriginChoice.HeadPosition) throw new QueueException(HostTexts.Of("Queue.HeadPositionOrigin", setup.Name));
        return setup;
    }

    /// <summary>The Job card's program as an entry before every other: its setup's file, or a copy of the whole file.</summary>
    private QueueEntry InsertCardJob()
    {
        var program = _control.WholeProgram ?? throw new QueueException(HostTexts.Of("Program.ChooseFirst"));
        var (setup, file, options) = (_control.Setup, _control.SetupFile, _control.Options.Clone());
        var first = Items.Count == 0 ? 1 : Items.Min(i => i.Entry.Position) - 1;
        return file is not null && file.SetupId == setup?.Id
            ? Insert(setup.Id, file.Id, null, null, null, 1, options, first)
            : InsertCopy(setup?.Id, program.Source.FileName, program.Content, 1, options, first);
    }

    private QueueEntry InsertCopy(long? setupId, string fileName, byte[] content, int repeat, JobStartOptions? options = null, int? position = null)
    {
        var stored = StoredFiles.Store(_directory, fileName, content);
        try
        {
            return Insert(setupId, null, stored, StoredFiles.FileNameOf(fileName), GCodeCache.Md5Of(content), repeat, options, position);
        }
        catch
        {
            File.Delete(Path.Combine(_directory, stored));
            throw;
        }
    }

    /// <param name="position">Where it goes; null: after every other entry.</param>
    private QueueEntry Insert(long? setupId, long? fileId, string? stored, string? originalFileName, string? md5, int repeat,
        JobStartOptions? options = null, int? position = null)
    {
        var at = position ?? (Items.Count == 0 ? 1 : Items.Max(i => i.Entry.Position) + 1);
        var id = _repository.Insert(_session.Address, at, setupId, fileId, stored, originalFileName, md5, Math.Clamp(repeat, 1, MaxRepeat),
            DateTime.UtcNow, options);
        Reload();
        return Get(id);
    }

    /// <summary>A job from the Job card that never ran: it goes as if it had not been queued, and the queue stops again.</summary>
    private void Discard(QueueEntry entry)
    {
        lock (_lock)
        {
            Current = null;
            Running = false;
        }
        DeleteCopy(entry);
        _repository.Remove(entry.Id);
        Reload();
    }

    private void SetState(QueueEntry entry, QueueEntryState state, int done, string? error, int? restartSegment)
    {
        if (error is null) _failures.TryRemove(entry.Id, out _);
        _repository.UpdateState(entry.Id, state, done, error, restartSegment);
        Reload();
    }

    private QueueEntry Get(long id) => _repository.Get(id) ?? throw new QueueException(HostTexts.Of("Queue.EntryGone"));

    private QueueEntry NotRunning(long id)
    {
        var entry = Get(id);
        return entry.Id == Current?.Id ? throw new QueueException(HostTexts.Of("Queue.EntryRunning")) : entry;
    }

    private string? CopyPath(QueueEntry entry) =>
        entry.StoredFile is { } stored && Path.Combine(_directory, stored) is { } path && File.Exists(path) ? path : null;

    private void DeleteCopy(QueueEntry entry)
    {
        if (CopyPath(entry) is { } path) File.Delete(path);
        if (entry.StoredFile is not null) _repository.ClearStoredFile(entry.Id);
    }

    /// <summary>The entry for people: setup and file, or what is known of it.</summary>
    private Text Label(QueueEntry entry) =>
        (Items.FirstOrDefault(i => i.Entry.Id == entry.Id)?.Label ?? entry.OriginalFileName) is { } label ? Text.Plain(label) : HostTexts.Of("Queue.Job");

    private IReadOnlyList<QueueItem> Build()
    {
        var setups = _library.List(includeDeleted: true);
        var names = setups.ToDictionary(e => e.Setup.Id, e => e.Setup.Name);
        var files = setups.SelectMany(e => e.Files).ToDictionary(f => f.File.Id);
        return _repository.List(_session.Address).Select(e => WithFailure(ItemOf(e, names, files))).ToList();
    }

    private QueueItem ItemOf(QueueEntry e, Dictionary<long, string> names, Dictionary<long, SetupFileEntry> files)
    {
        var setupName = e.SetupId is { } setupId ? names.GetValueOrDefault(setupId, "?") : null;
        if (e.FileId is { } fileId && files.TryGetValue(fileId, out var file))
            return new QueueItem(e, setupName, file.File.OriginalFileName, file.FileMissing || file.File.IsDeleted);
        return new QueueItem(e, setupName, e.OriginalFileName ?? "?", e.FileId is not null || CopyPath(e) is null);
    }

    private QueueItem WithFailure(QueueItem item) =>
        item.Entry.Error is not null && _failures.TryGetValue(item.Entry.Id, out var reason) ? item with { Error = reason } : item;

    /// <summary>Lists again on the next read: the entries or the names changed.</summary>
    private void Reload()
    {
        _items = null;
        Changed?.Invoke();
    }
}

/// <summary>One <see cref="JobQueue"/> per machine session, kept while the app runs.</summary>
public sealed class JobQueues(JobControls controls, JobTrackers trackers, QueueRepository repository, SetupLibrary library,
    NotificationCenter notifications, AppPaths paths)
{
    private readonly ConcurrentDictionary<string, JobQueue> _queues = new(StringComparer.OrdinalIgnoreCase);

    public JobQueue Get(MachineSession session) => _queues.GetOrAdd(session.Address, _ =>
        new JobQueue(session, controls.Get(session), trackers.Get(session), repository, library, notifications.Get(session), paths.QueuedFiles));
}
