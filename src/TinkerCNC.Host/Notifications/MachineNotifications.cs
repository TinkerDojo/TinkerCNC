namespace TinkerCNC.Host.Notifications;

/// <summary>
/// The notifications of one machine, newest first. They are collected from the session's status reports whether or
/// not a dashboard is open; open ones count towards the badge until they are answered, dismissed or resolved by the
/// machine itself.
/// </summary>
public sealed class MachineNotifications : IDisposable
{
    private const int MaxKept = 50;

    private static readonly IReadOnlyList<INotificationSource> Sources = [new ToolChangeSource(), new AlarmSource()];

    private readonly object _gate = new();
    private readonly List<IMachineNotification> _items = new();
    private readonly MachineSession _session;
    private readonly JobTracker _job;
    private StatusReport? _lastStatus;

    public MachineNotifications(MachineSession session, JobTracker job)
    {
        _session = session;
        _job = job;
        _session.Changed += OnSessionChanged;
    }

    public event Action? Changed;

    /// <summary>A new notification came in (from the machine's status or a host service), after it was added.</summary>
    public event Action<IMachineNotification>? Raised;

    public IReadOnlyList<IMachineNotification> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public int OpenCount
    {
        get { lock (_gate) return _items.Count(n => n.IsOpen); }
    }

    /// <summary>Runs an action of a notification on the machine and resolves the notification when it succeeded.</summary>
    public async Task ExecuteAsync(IMachineNotification notification, NotificationAction action)
    {
        await action.Execute(_session);
        notification.Resolve(action.Resolution);
        Changed?.Invoke();
    }

    /// <summary>Adds a notification raised by a host service (the queue), not by the machine's status.</summary>
    public void Raise(IMachineNotification notification)
    {
        lock (_gate)
        {
            _items.Insert(0, notification);
            if (_items.Count > MaxKept) _items.RemoveRange(MaxKept, _items.Count - MaxKept);
        }
        Changed?.Invoke();
        Raised?.Invoke(notification);
    }

    public void Dismiss(IMachineNotification notification)
    {
        notification.Resolve(HostTexts.Of("Notice.Dismissed"));
        Changed?.Invoke();
    }

    public void ClearResolved()
    {
        lock (_gate) _items.RemoveAll(n => !n.IsOpen);
        Changed?.Invoke();
    }

    private void OnSessionChanged()
    {
        var status = _session.Status;
        if (status is null || ReferenceEquals(status, _lastStatus)) return;
        List<IMachineNotification> raised;
        lock (_gate)
        {
            foreach (var item in _items) item.Observe(status);
            raised = Sources.Select(s => s.Observe(_lastStatus, status, _session, _job)).OfType<IMachineNotification>().ToList();
            foreach (var notification in raised) _items.Insert(0, notification);
            if (_items.Count > MaxKept) _items.RemoveRange(MaxKept, _items.Count - MaxKept);
            _lastStatus = status;
        }
        Changed?.Invoke();
        foreach (var notification in raised) Raised?.Invoke(notification);
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}

/// <summary>
/// One <see cref="MachineNotifications"/> per machine session, created with the session; <see cref="Raised"/> and
/// <see cref="Changed"/> report every machine's notifications in one place (for system notifications).
/// </summary>
public sealed class NotificationCenter : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MachineNotifications> _machines = new(StringComparer.OrdinalIgnoreCase);
    private readonly JobTrackers _jobs;

    public NotificationCenter(MachineSessionManager sessions, JobTrackers jobs)
    {
        _jobs = jobs;
        sessions.SessionCreated += s => Get(s);
    }

    /// <summary>A machine got a new notification.</summary>
    public event Action<MachineSession, IMachineNotification>? Raised;

    /// <summary>Some machine's notifications changed (added, answered, resolved, cleared).</summary>
    public event Action? Changed;

    // Created under the lock: a second instance would also watch the session and raise everything twice.
    public MachineNotifications Get(MachineSession session)
    {
        lock (_gate)
        {
            if (_machines.TryGetValue(session.Address, out var existing)) return existing;
            var created = new MachineNotifications(session, _jobs.Get(session));
            created.Raised += notification => Raised?.Invoke(session, notification);
            created.Changed += () => Changed?.Invoke();
            _machines[session.Address] = created;
            return created;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var m in _machines.Values) m.Dispose();
        }
    }
}
