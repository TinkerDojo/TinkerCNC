using System.Collections.Concurrent;
using System.Globalization;
using TinkerCNC.Motion;
using TinkerCNC.Protocol;
using TinkerCNC.Transfer;
using TinkerCNC.Transport;

namespace TinkerCNC.Host;

public enum SessionState { Disconnected, Connecting, Connected, Failed }

public enum LogKind { Sent, Received, Alarm, System, Error }

public sealed record LogLine(DateTime Time, LogKind Kind, string Text);

/// <summary>A connection to one machine, shared by every browser tab that opens it.</summary>
public sealed class MachineSession : IAsyncDisposable
{
    private const int MaxLog = 1000;
    private readonly LinkedList<LogLine> _log = new();
    private readonly object _logLock = new();
    private MachineConnection? _machine;

    internal MachineSession(string address, string name)
    {
        Address = address;
        Name = name;
        Host = TcpTransport.Parse(address).Host;
        Air = new AirControl(this);
        BedCleaning = new BedCleaning(this);
        Limits = new MachineLimits(this);
    }

    /// <summary>The machine's travel (read after connecting).</summary>
    private readonly SemaphoreSlim _exchange = new(1, 1);

    public MachineLimits Limits { get; }

    /// <summary>The machine's compressed air.</summary>
    public AirControl Air { get; }

    /// <summary>Sweeping the bed with the air on, also in the middle of a job.</summary>
    public BedCleaning BedCleaning { get; }

    public string Address { get; }
    public string Host { get; }
    public string Name { get; internal set; }
    public SessionState State { get; private set; }

    /// <summary>Why the connection failed or dropped, or null.</summary>
    public Text? Error { get; private set; }
    public StatusReport? Status { get; private set; }
    public DateTime? StatusUtc { get; private set; }
    public string? LastAlarm { get; private set; }
    public DateTime? ConnectedSinceUtc { get; private set; }
    public bool LogStatusReports { get; set; }

    /// <summary>Raised on any change (status, log, connection). Handlers must be cheap.</summary>
    public event Action? Changed;

    /// <summary>Raised for every text reply of the machine (command answers, messages).</summary>
    public event Action<string>? TextReceived;

    public IReadOnlyList<LogLine> Log
    {
        get { lock (_logLock) return _log.ToList(); }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is SessionState.Connected or SessionState.Connecting) return;
        State = SessionState.Connecting;
        Error = null;
        Append(LogKind.System, $"Connecting to {Address}…");

        var machine = MachineConnection.ForAddress(Address);
        machine.StatusReceived += s =>
        {
            Status = s;
            StatusUtc = DateTime.UtcNow;
            if (LogStatusReports) Append(LogKind.Received, s.Raw, notify: false);
            Changed?.Invoke();
        };
        var logger = new MessageLogger(this);
        machine.MessageReceived += m => m.Accept(logger);
        machine.Disconnected += ex =>
        {
            State = SessionState.Failed;
            Error = ex is null ? HostTexts.Of("Session.ClosedByMachine") : Text.Of(ex);
            ConnectedSinceUtc = null;
            Append(LogKind.Error, $"Disconnected: {Error}");
        };

        try
        {
            await machine.ConnectAsync(ct);
            _machine = machine;
            State = SessionState.Connected;
            ConnectedSinceUtc = DateTime.UtcNow;
            Append(LogKind.System, $"Connected via {machine.Description}");
            _ = Limits.LoadAsync();
        }
        catch (Exception ex)
        {
            await machine.DisposeAsync();
            State = SessionState.Failed;
            Error = Text.Of(ex);
            Append(LogKind.Error, $"Connection failed: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        await BedCleaning.StopForDisconnectAsync();
        await Air.OffIfOnAsync();
        Limits.ForgetError();
        var m = _machine;
        _machine = null;
        if (m is not null) await m.DisposeAsync();
        State = SessionState.Disconnected;
        Status = null;
        ConnectedSinceUtc = null;
        Append(LogKind.System, "Disconnected");
    }

    public async Task SendAsync(string command)
    {
        var m = RequireCommandLine();
        Append(LogKind.Sent, command);
        await m.SendCommandAsync(command);
    }

    public async Task RealtimeAsync(RealtimeCommand command, string label)
    {
        var m = RequireCommandLine();
        Append(LogKind.Sent, label);
        await m.SendRealtimeAsync(command);
    }

    public Task FeedHoldAsync() => RealtimeAsync(RealtimeCommand.FeedHold, "! (feed hold)");
    public Task CycleStartAsync() => RealtimeAsync(RealtimeCommand.CycleStart, "~ (cycle start / resume)");
    public Task ResetAsync() => RealtimeAsync(RealtimeCommand.Reset, "Ctrl-X (reset)");

    // Grbl-style system commands: home all axes, clear the alarm lock.
    public Task HomeAsync() => SendAsync("$H");
    public Task UnlockAsync() => SendAsync("$X");

    /// <summary>
    /// Relative jog as a rapid move in incremental mode ("G91 G0 " + axis words), in millimetres whatever units the
    /// last program left (<see cref="MachineUnits"/>). G90 is restored afterwards so later absolute commands are not
    /// affected.
    /// </summary>
    public async Task JogAsync(double dx = 0, double dy = 0, double dz = 0, double da = 0)
    {
        static string W(char axis, double v) => v == 0 ? "" : $" {axis}{v.ToString("0.###", CultureInfo.InvariantCulture)}";
        var words = W('X', dx) + W('Y', dy) + W('Z', dz) + W('A', da);
        if (words.Length == 0) return;
        await SendAsync(MachineUnits.Millimetres);
        await SendAsync("G91 G0" + words);
        await SendAsync("G90");
    }

    /// <summary>
    /// Rapid move to a typed position (see <see cref="GoToPlanner"/>), only while idle and only within the travel read
    /// from the machine. The check is repeated here with the latest status, whatever the page showed.
    /// </summary>
    public async Task GoToAsync(GoToRequest request)
    {
        if (Limits.Travel is not { } travel)
            throw new LocalizedException(Limits.Error ?? HostTexts.Of("Limits.Unknown"));
        if (Status is not { } status || !IsIdle(status))
            throw new LocalizedException(HostTexts.Of("GoTo.NotIdle", Status?.State is { } state ? Text.Plain(state) : HostTexts.Of("State.Unknown")));
        var plan = GoToPlanner.Plan(request, status, travel);
        if (!plan.IsValid) throw new LocalizedException(plan.Errors.Values.Aggregate((a, b) => HostTexts.Of("Text.Sentences", a, b)));
        foreach (var command in plan.Commands) await SendAsync(command);
    }

    public static bool IsIdle(StatusReport? status) =>
        status?.State?.StartsWith("Idle", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The state the machine reports, as it reports it, for messages; "not reporting" without a status.</summary>
    public static Text StateOf(StatusReport? status) =>
        status?.State is { } state ? Text.Plain(state) : HostTexts.Of("State.NotReporting");

    /// <summary>Asks the machine which file it is running (0xB7).</summary>
    public Task<RunningFile?> QueryRunningFileAsync(CancellationToken ct = default) =>
        ExclusiveAsync(m => m.QueryRunningFileAsync(TimeSpan.FromSeconds(3), ct), ct);

    /// <summary>Downloads a file from the machine's SD card; with a cache, an identical local copy is used instead.</summary>
    public async Task<DownloadResult> DownloadAsync(
        string remotePath, IProgress<DownloadProgress>? progress = null, IFileCache? cache = null, CancellationToken ct = default)
    {
        Append(LogKind.System, $"Downloading {remotePath}…");
        var result = await ExclusiveAsync(m => new FileDownloader(m).DownloadAsync(remotePath, progress, ct, cache), ct);
        Append(LogKind.System, result.FromCache
            ? $"{remotePath} is unchanged (MD5 {result.Md5}): using the cached copy"
            : $"Downloaded {remotePath} ({result.Content.Length:N0} bytes, MD5 {(result.Md5Matches is false ? "mismatch" : "ok")})");
        return result;
    }

    /// <summary>
    /// Uploads a file to the machine's SD card, replacing a file of that name, unless the machine already has it with
    /// the same content: the MD5 it announces for that path (asked without transferring anything) is compared first,
    /// so running the same program again does not send it again. Returns whether the file was transferred.
    /// </summary>
    public async Task<bool> UploadAsync(string remotePath, byte[] content, IProgress<UploadProgress>? progress = null, CancellationToken ct = default)
    {
        var md5 = GCodeCache.Md5Of(content);
        var transferred = await ExclusiveAsync(async m =>
        {
            var onMachine = await new FileDownloader(m) { MaxRetries = Md5ProbeRetries }.AnnouncedMd5Async(remotePath, ct);
            if (string.Equals(onMachine, md5, StringComparison.OrdinalIgnoreCase)) return false;
            Append(LogKind.System, $"Uploading {remotePath} ({content.Length:N0} bytes)…");
            await new FileUploader(m).UploadAsync(remotePath, content, progress, ct);
            return true;
        }, ct);
        Append(LogKind.System, transferred
            ? $"Uploaded {remotePath}"
            : $"{remotePath} is already on the machine (MD5 {md5}): not uploaded again");
        return transferred;
    }

    /// <summary>The MD5 question is only a shortcut: without a quick answer the file is simply uploaded.</summary>
    private const int Md5ProbeRetries = 1;

    /// <summary>Lists a directory on the machine's SD card.</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListFilesAsync(string directory, CancellationToken ct = default)
    {
        Append(LogKind.Sent, RemoteListing.Command(directory));
        return await ExclusiveAsync(m => new RemoteDirectoryReader(m).ListAsync(directory, ct), ct);
    }

    /// <summary>A file transfer or listing is running: the machine reads nothing else meanwhile.</summary>
    public bool Transferring { get; private set; }

    /// <summary>
    /// Runs one exchange that reads the machine's replies (file transfer, listing, running-file query) at a time:
    /// replies can't be told apart, and during a transfer the machine treats every packet as part of it (the
    /// firmware, Player upload/download). Others wait their turn.
    /// </summary>
    private async Task<T> ExclusiveAsync<T>(Func<MachineConnection, Task<T>> exchange, CancellationToken ct)
    {
        await _exchange.WaitAsync(ct);
        try
        {
            Transferring = true;
            return await exchange(RequireMachine());
        }
        finally
        {
            Transferring = false;
            _exchange.Release();
        }
    }

    /// <summary>The machine, when it can take a command now: during a transfer it would silently drop it.</summary>
    private MachineConnection RequireCommandLine() => Transferring
        ? throw new LocalizedException(HostTexts.Of("Session.Transferring"))
        : RequireMachine();

    private MachineConnection RequireMachine() =>
        _machine is { IsConnected: true } m ? m : throw new LocalizedException(HostTexts.Of("Machine.NotConnected"));

    private void Append(LogKind kind, string text, bool notify = true)
    {
        lock (_logLock)
        {
            _log.AddLast(new LogLine(DateTime.Now, kind, text));
            while (_log.Count > MaxLog) _log.RemoveFirst();
        }
        if (notify) Changed?.Invoke();
    }

    /// <summary>
    /// Waits for a status report that satisfies <paramref name="condition"/> (checked against the current one first).
    /// </summary>
    /// <exception cref="TimeoutException">No such report arrived within <paramref name="timeout"/>.</exception>
    public async Task<StatusReport> WaitForStatusAsync(Func<StatusReport, bool> condition, TimeSpan timeout, CancellationToken ct = default)
    {
        var match = new TaskCompletionSource<StatusReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (Status is { } status && condition(status)) match.TrySetResult(status);
        }

        Changed += Check;
        try
        {
            Check();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                return await match.Task.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LocalizedTimeoutException(HostTexts.Of("Session.StateTimeout", timeout.TotalSeconds));
            }
        }
        finally
        {
            Changed -= Check;
        }
    }

    /// <summary>Sends a command and returns the first text reply that <paramref name="isReply"/> accepts.</summary>
    /// <exception cref="TimeoutException">No such reply arrived within <paramref name="timeout"/>.</exception>
    public async Task<string> QueryAsync(string command, Func<string, bool> isReply, TimeSpan timeout, CancellationToken ct = default)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(string text)
        {
            if (isReply(text)) reply.TrySetResult(text);
        }

        TextReceived += Check;
        try
        {
            await SendAsync(command);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                return await reply.Task.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LocalizedTimeoutException(HostTexts.Of("Session.ReplyTimeout", command, timeout.TotalSeconds));
            }
        }
        finally
        {
            TextReceived -= Check;
        }
    }

    /// <summary>Reads one setting of the machine's configuration; null when it is not set.</summary>
    public async Task<string?> GetConfigAsync(string key, CancellationToken ct = default)
    {
        var reply = await QueryAsync(MachineConfig.GetCommand(key), text => MachineConfig.IsReplyFor(text, key), TimeSpan.FromSeconds(5), ct);
        return MachineConfig.ValueIn(reply, key);
    }

    /// <summary>For helpers such as <see cref="AirControl"/> whose state is part of the session.</summary>
    internal void NotifyChanged() => Changed?.Invoke();

    /// <summary>Adds an informational line to the console log.</summary>
    public void Note(string text) => Append(LogKind.System, text);

    public void ClearLog()
    {
        lock (_logLock) _log.Clear();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_machine is not null) await _machine.DisposeAsync();
    }

    /// <summary>Writes incoming messages to the console log (status reports are handled separately).</summary>
    private sealed class MessageLogger(MachineSession session) : MachineMessageVisitor
    {
        public override void Visit(InfoMessage message)
        {
            session.Append(LogKind.Received, message.Text.TrimEnd());
            session.TextReceived?.Invoke(message.Text);
        }

        public override void Visit(AlarmMessage message)
        {
            session.LastAlarm = message.Text.Trim();
            session.Append(LogKind.Alarm, message.Text.TrimEnd());
        }

        public override void Visit(DiagnoseMessage message) => Tagged("Diagnose", message);
        public override void Visit(LoadInfoMessage message) => Tagged("LoadInfo", message);
        public override void Visit(LoadFinishedMessage message) => Tagged("LoadFinished", message);
        public override void Visit(LoadErrorMessage message) => session.Append(LogKind.Error, $"[LoadError] {message.Text.TrimEnd()}");

        private void Tagged(string tag, IMachineMessage message) =>
            session.Append(LogKind.Received, $"[{tag}] {message.Text.TrimEnd()}");
    }
}

public sealed class MachineSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MachineSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;
    public event Action<MachineSession>? SessionCreated;

    public MachineSession GetOrCreate(string address, string name)
    {
        var created = false;
        var s = _sessions.GetOrAdd(address, a =>
        {
            created = true;
            var session = new MachineSession(a, name);
            session.Changed += () => Changed?.Invoke();
            return session;
        });
        if (created) SessionCreated?.Invoke(s);
        if (!string.IsNullOrWhiteSpace(name)) s.Name = name;
        return s;
    }

    public MachineSession? Find(string address) => _sessions.TryGetValue(address, out var s) ? s : null;

    public IReadOnlyList<MachineSession> Connected =>
        _sessions.Values.Where(s => s.State is SessionState.Connected or SessionState.Connecting).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values) await s.DisposeAsync();
    }
}
