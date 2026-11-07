using System.Collections.Concurrent;
using TinkerCNC.Host.Notifications;
using TinkerCNC.Jobs;
using TinkerCNC.Programs;
using TinkerCNC.Queue;

namespace TinkerCNC.Host;

/// <summary>The queue can't do that right now.</summary>
public sealed class QueueException(string message) : InvalidOperationException(message);

/// <summary>A queue entry as shown: with the names its ids stand for.</summary>
public sealed record QueueItem(QueueEntry Entry, string SetupName, string FileName, bool FileMissing)
{
    public string Label => $"{SetupName} · {FileName}";
}

/// <summary>
/// The job queue of one machine: its entries (<see cref="QueueEntry"/>) run one after the other, each with the options
/// of its setup as they are when it starts, and each beginning with a pause (<see cref="JobCommands.Pause"/>) so the
/// machine waits for the go: the machine's button held, or Resume here. A failure (an alarm, Stop, a refused start, the
/// connection lost, a job that ended before its last operation) stops the queue with the entry marked failed; a
/// retry can begin at the tool operation that was interrupted (<see cref="JobControl.StartFrom"/>). The entries and
/// the copies queued for one run only live in the database and the queued files folder, so a queue survives a
/// restart of the app, stopped.
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
    private IReadOnlyList<QueueItem>? _items;
    private bool _wasPlaying;
    private bool _gateRaised;
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
    public string? Error { get; private set; }

    /// <summary>Every entry, done ones included, in queue order.</summary>
    public IReadOnlyList<QueueItem> Items => _items ??= Build();

    /// <summary>The entries still listed: everything but the done ones.</summary>
    public IReadOnlyList<QueueItem> Listed => Items.Where(i => i.Entry.State != QueueEntryState.Done).ToList();

    /// <summary>Runs that finished, over every entry.</summary>
    public int JobsDone => Items.Sum(i => i.Entry.Done);

    /// <summary>Runs in the queue altogether, finished ones included.</summary>
    public int JobsTotal => Items.Sum(i => i.Entry.Repeat);

    /// <summary>Whether a queued job is paused right after its start, waiting for the go.</summary>
    public bool WaitingForGo => Current is not null && JobSuspension.IsSuspended(_session.Status) && _tracker.Preparing;

    /// <summary>The item whose job is on the machine.</summary>
    public QueueItem? CurrentItem => Current is { } current ? Items.FirstOrDefault(i => i.Entry.Id == current.Id) : null;

    /// <summary>Queues one of a setup's files.</summary>
    /// <exception cref="QueueException">The setup can't be queued (its work origin is the head position).</exception>
    public QueueEntry AddSetupFile(long fileId, int repeat)
    {
        var file = _library.GetFile(fileId);
        var setup = Queueable(_library.Get(file.SetupId));
        if (file.IsDeleted) throw new QueueException($"\"{file.OriginalFileName}\" was deleted from the setup.");
        return Insert(setup, file.Id, null, null, null, repeat);
    }

    /// <summary>Queues a copy of a file for its runs only, with a setup's options.</summary>
    public QueueEntry AddCopy(long setupId, string fileName, byte[] content, int repeat)
    {
        var setup = Queueable(_library.Get(setupId));
        var stored = StoredFiles.Store(_directory, fileName, content);
        try
        {
            return Insert(setup, null, stored, StoredFiles.FileNameOf(fileName), GCodeCache.Md5Of(content), repeat);
        }
        catch
        {
            File.Delete(Path.Combine(_directory, stored));
            throw;
        }
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

    public void SetRepeat(long id, int repeat)
    {
        var entry = NotRunning(id);
        _repository.UpdateRepeat(entry.Id, Math.Clamp(repeat, 1, MaxRepeat));
        Reload();
    }

    /// <summary>A failed entry gets its next run, from the whole file or from a tool operation (its index).</summary>
    public void Retry(long id, int? fromSegment)
    {
        var entry = Get(id);
        if (entry.State != QueueEntryState.Failed) throw new QueueException("Only a failed entry is retried.");
        _repository.UpdateState(id, QueueEntryState.Pending, entry.Done, null, fromSegment is > 0 ? fromSegment : null);
        Reload();
    }

    /// <summary>A queued copy becomes one of its setup's files, so it stays after the runs.</summary>
    public void KeepInSetup(long id)
    {
        var entry = Get(id);
        if (!entry.IsQueuedCopy) throw new QueueException("That file is one of the setup's files already.");
        var path = CopyPath(entry) ?? throw new QueueException("The queued copy is gone.");
        var file = _library.AddFile(entry.SetupId, entry.OriginalFileName ?? "program.nc", File.ReadAllBytes(path));
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

    /// <summary>Starts the first pending entry and goes on from there.</summary>
    /// <exception cref="QueueException">The machine can't start a job right now.</exception>
    public void Start()
    {
        lock (_lock)
        {
            if (Running) return;
            if (_session.State != SessionState.Connected) throw new QueueException("The machine is not connected.");
            if (Current is null && !JobControl.CanStartIn(_session.Status))
                throw new QueueException($"The machine has to be Idle to start the queue (it is {_session.Status?.State ?? "not reporting"}).");
            Running = true;
            Error = null;
        }
        Changed?.Invoke();
        _ = Task.Run(RunNextAsync);
    }

    /// <summary>Nothing further starts; the running job is stopped too (its entry fails as "stopped").</summary>
    public async Task StopAsync()
    {
        lock (_lock) Running = false;
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
                _notifications.Raise(new QueueNotification("Queue finished", $"Every job in the queue ran ({JobsDone}).", "info", "check"));
                Changed?.Invoke();
                return;
            }
            Current = entry;
            _gateRaised = false;
        }
        SetState(entry, QueueEntryState.Running, entry.Done, null, entry.RestartSegment);
        try
        {
            var (setup, file, name, content) = Resolve(entry);
            _control.LoadQueued(setup, file, name, content, entry.RestartSegment);
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
            Fail(entry, ex.Message);
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
        lock (_lock)
        {
            var connected = _session.State == SessionState.Connected;
            var playing = connected && _session.Status?.IsPlaying == true;
            var wasPlaying = _wasPlaying;
            _wasPlaying = playing;
            if (Current is not { } current)
            {
                if (!Running || connected) return;
                Running = false;
                Error = "The connection was lost.";
            }
            else if (wasPlaying && !playing)
            {
                ended = current;
            }
            else if (WaitingForGo && !_gateRaised)
            {
                _gateRaised = true;
                _notifications.Raise(new QueueGateNotification($"{Label(current)} (job {JobsDone + 1} of {JobsTotal})"));
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
        Changed?.Invoke();
    }

    /// <summary>Why the job that just ended did not finish, or null when it did.</summary>
    private string? EndReason()
    {
        if (_session.State != SessionState.Connected) return "The connection was lost.";
        if (JobSuspension.HasState(_session.Status, "Alarm")) return $"The machine raised an alarm: {_session.LastAlarm ?? "no message"}.";
        if (_control.StopRequestedUtc > _startedUtc) return "Stopped.";
        if (_tracker.LastRun is { EndedEarly: true } run)
            return "The job ended before its last operation" + (run.Interrupted is { } at ? $", at {at.Label}." : ".");
        return null;
    }

    private void Succeed(QueueEntry entry)
    {
        var done = entry.Done + 1;
        var finished = done >= entry.Repeat;
        if (finished) DeleteCopy(entry);
        SetState(entry, finished ? QueueEntryState.Done : QueueEntryState.Pending, done, null, null);
        _session.Note($"Queue: {Label(entry)} finished ({done} of {entry.Repeat}).");
        lock (_lock) Current = null;
        Reload();
        _ = Task.Run(RunNextAsync); // off the session's thread: the next start sends commands and waits
    }

    private void Fail(QueueEntry entry, string reason)
    {
        var interrupted = _tracker.LastRun is { EndedEarly: true, EndedUtc: var at } run && at >= _startedUtc ? run.Interrupted?.Index : null;
        SetState(entry, QueueEntryState.Failed, entry.Done, reason, interrupted is > 0 ? interrupted : null);
        lock (_lock)
        {
            Current = null;
            Running = false;
            Error = $"{Label(entry)}: {reason}";
        }
        _session.Note($"Queue stopped: {Label(entry)}: {reason}");
        _notifications.Raise(new QueueNotification("Queue stopped", $"{Label(entry)}: {reason} Retry the entry (also from the operation it " +
            "was at) or remove it, then start the queue again.", "alarm", "alert"));
        Reload();
    }

    private (Setup Setup, SetupFile? File, string Name, byte[] Content) Resolve(QueueEntry entry)
    {
        var setup = Queueable(_library.Find(entry.SetupId) ?? throw new QueueException("Its setup no longer exists."));
        if (entry.FileId is { } fileId)
        {
            var file = _library.FindFile(fileId);
            if (file is null || file.IsDeleted) throw new QueueException("Its file was deleted from the setup.");
            return (setup, file, file.OriginalFileName, _library.ReadFile(file));
        }
        var path = CopyPath(entry) ?? throw new QueueException("The queued copy of its file is missing.");
        return (setup, null, entry.OriginalFileName ?? "program.nc", File.ReadAllBytes(path));
    }

    private static Setup Queueable(Setup setup)
    {
        if (setup.IsDeleted) throw new QueueException($"\"{setup.Name}\" was deleted; restore it first.");
        if (setup.Options.WorkOrigin == WorkOriginChoice.HeadPosition)
            throw new QueueException($"\"{setup.Name}\" takes the work origin from where the head is, which is wherever the previous job " +
                                     "ended in a queue. Set the origin by hand once and let the setup keep it, or use the bed corner or the XYZ probe.");
        return setup;
    }

    private QueueEntry Insert(Setup setup, long? fileId, string? stored, string? originalFileName, string? md5, int repeat)
    {
        var position = Items.Count == 0 ? 1 : Items.Max(i => i.Entry.Position) + 1;
        var id = _repository.Insert(_session.Address, position, setup.Id, fileId, stored, originalFileName, md5, Math.Clamp(repeat, 1, MaxRepeat),
            DateTime.UtcNow);
        Reload();
        return Get(id);
    }

    private void SetState(QueueEntry entry, QueueEntryState state, int done, string? error, int? restartSegment)
    {
        _repository.UpdateState(entry.Id, state, done, error, restartSegment);
        Reload();
    }

    private QueueEntry Get(long id) => _repository.Get(id) ?? throw new QueueException("That entry is gone.");

    private QueueEntry NotRunning(long id)
    {
        var entry = Get(id);
        return entry.Id == Current?.Id ? throw new QueueException("That entry's job is on the machine.") : entry;
    }

    private string? CopyPath(QueueEntry entry) =>
        entry.StoredFile is { } stored && Path.Combine(_directory, stored) is { } path && File.Exists(path) ? path : null;

    private void DeleteCopy(QueueEntry entry)
    {
        if (CopyPath(entry) is { } path) File.Delete(path);
        if (entry.StoredFile is not null) _repository.ClearStoredFile(entry.Id);
    }

    private string Label(QueueEntry entry) => Items.FirstOrDefault(i => i.Entry.Id == entry.Id)?.Label ?? entry.OriginalFileName ?? "job";

    private IReadOnlyList<QueueItem> Build()
    {
        var setups = _library.List(includeDeleted: true);
        var names = setups.ToDictionary(e => e.Setup.Id, e => e.Setup.Name);
        var files = setups.SelectMany(e => e.Files).ToDictionary(f => f.File.Id);
        return _repository.List(_session.Address).Select(e =>
        {
            if (e.FileId is { } fileId && files.TryGetValue(fileId, out var file))
                return new QueueItem(e, names.GetValueOrDefault(e.SetupId, "?"), file.File.OriginalFileName, file.FileMissing || file.File.IsDeleted);
            return new QueueItem(e, names.GetValueOrDefault(e.SetupId, "?"), e.OriginalFileName ?? "?", e.FileId is not null || CopyPath(e) is null);
        }).ToList();
    }

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
