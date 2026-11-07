using System.Collections.Concurrent;

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
    }

    public void Dismiss(IMachineNotification notification)
    {
        notification.Resolve("Dismissed");
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
        lock (_gate)
        {
            foreach (var item in _items) item.Observe(status);
            foreach (var raised in Sources.Select(s => s.Observe(_lastStatus, status, _session, _job)).OfType<IMachineNotification>())
                _items.Insert(0, raised);
            if (_items.Count > MaxKept) _items.RemoveRange(MaxKept, _items.Count - MaxKept);
            _lastStatus = status;
        }
        Changed?.Invoke();
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}

/// <summary>One <see cref="MachineNotifications"/> per machine session, created with the session.</summary>
public sealed class NotificationCenter : IDisposable
{
    private readonly ConcurrentDictionary<string, MachineNotifications> _machines = new(StringComparer.OrdinalIgnoreCase);
    private readonly JobTrackers _jobs;

    public NotificationCenter(MachineSessionManager sessions, JobTrackers jobs)
    {
        _jobs = jobs;
        sessions.SessionCreated += s => Get(s);
    }

    public MachineNotifications Get(MachineSession session) =>
        _machines.GetOrAdd(session.Address, _ => new MachineNotifications(session, _jobs.Get(session)));

    public void Dispose()
    {
        foreach (var m in _machines.Values) m.Dispose();
    }
}
