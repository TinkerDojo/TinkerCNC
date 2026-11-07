using System.Collections.Concurrent;
using TinkerCNC.GCode;
using TinkerCNC.Jobs;
using TinkerCNC.Programs;
using TinkerCNC.Transfer;

namespace TinkerCNC.Host;

/// <summary>Where a program to start comes from.</summary>
public interface IProgramSource
{
    string FileName { get; }

    /// <summary>Where the program is run from on the machine's SD card.</summary>
    string MachinePath { get; }

    /// <summary>Whether starting it transfers the file to the machine first.</summary>
    bool NeedsUpload { get; }

    string Describe();

    /// <summary>Makes sure the program is on the machine's SD card.</summary>
    Task PutOnMachineAsync(MachineSession session, IProgress<UploadProgress> progress);
}

/// <summary>A file kept here (from this computer, a setup or the queue): uploaded to the programs folder before it starts.</summary>
/// <param name="Origin">Where it comes from, for <see cref="Describe"/>.</param>
public sealed record LocalProgram(string FileName, byte[] Content, string Origin = "from this computer") : IProgramSource
{
    public string MachinePath => RemotePath.ForProgram(FileName);

    public bool NeedsUpload => true;

    public string Describe() => $"{Origin}, uploaded to {MachinePath} when started";

    public Task PutOnMachineAsync(MachineSession session, IProgress<UploadProgress> progress) =>
        session.UploadAsync(MachinePath, Content, progress);
}

/// <summary>A file already on the machine.</summary>
public sealed record MachineProgram(string MachinePath) : IProgramSource
{
    public string FileName => RemotePath.FileName(MachinePath);

    public bool NeedsUpload => false;

    public string Describe() => $"on the machine, {MachinePath}";

    public Task PutOnMachineAsync(MachineSession session, IProgress<UploadProgress> progress) => Task.CompletedTask;
}

/// <summary>The program chosen to run, with its content and analysis.</summary>
public sealed record SelectedProgram(IProgramSource Source, byte[] Content, GCodeAnalysis Analysis)
{
    /// <summary>The extent of its cutting moves in work coordinates; null when it has none.</summary>
    public ToolpathBounds? Bounds { get; } = Analysis.Toolpath.CutBounds();

    /// <summary>Whether it changes to a tool of its own.</summary>
    public bool SelectsTool { get; } = Analysis.Segments.Any(s => s.ToolNumber is not null);
}

public enum JobControlPhase { None, Loading, Uploading, Starting }

/// <summary>
/// Starts, pauses and stops programs on one machine. A program is chosen from this computer or from the machine's
/// programs folder (<see cref="RemotePath.GCodeDirectory"/>); starting it uploads it if needed and sends the commands
/// of a <see cref="JobStartPlan"/> built from <see cref="Options"/>, which are saved per machine. Once the machine
/// plays it, the <see cref="JobTracker"/> follows it as any other job.
/// </summary>
public sealed partial class JobControl
{
    /// <summary>Longest wait for the machine to report the program playing after <c>play</c>.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly MachineSession _session;
    private readonly JobTracker _tracker;
    private readonly GCodeCache _cache;
    private readonly JobOptionsStore _store;
    private readonly SetupLibrary _library;
    private readonly IReadOnlyList<(Func<JobControl, bool> Blocks, Func<JobControl, string> Reason)> _startRules;
    private int _busy;

    public JobControl(MachineSession session, JobTracker tracker, GCodeCache cache, JobOptionsStore store, SetupLibrary library,
        CameraService camera)
    {
        _library = library;
        _library.Changed += OnLibraryChanged;
        _session = session;
        _tracker = tracker;
        _cache = cache;
        _store = store;
        Timelapse = new JobTimelapse(session, camera);
        Options = store.Get(session.Address);
        _startRules =
        [
            (c => c._session.State != SessionState.Connected, _ => "The machine is not connected."),
            (c => c.Program is null, _ => "Choose a program first."),
            (c => c.IsBusy, _ => "Busy with the previous request."),
            (c => c._session.Limits.Travel is null, c => c._session.Limits.Error ?? "The machine's travel limits are not known yet."),
            (c => c._session.BedCleaning.IsRunning, _ => "The bed is being cleaned."),
            (c => c._session.Transferring, _ => "The machine is busy with a file transfer."),
            (c => !CanStartIn(c._session.Status), c => $"Programs start only while the machine is Idle (it is {c._session.Status?.State ?? "not reporting"})."),
        ];
    }

    /// <summary>The options the next start uses; a form edits them in place. Loading a setup's file replaces them.</summary>
    public JobStartOptions Options { get; private set; }

    /// <summary>The time-lapse of the job started here, when <see cref="JobStartOptions.Timelapse"/> was on.</summary>
    public JobTimelapse Timelapse { get; }

    /// <summary>
    /// Pause right after the start until the user resumes (<see cref="JobCommands.Pause"/>); set by the queue for its
    /// jobs, so the machine waits for the go before anything moves.
    /// </summary>
    public bool WaitForGo { get; set; }

    /// <summary>When Stop was last requested here, so a job that was stopped can be told from one that finished.</summary>
    public DateTime? StopRequestedUtc { get; private set; }

    public SelectedProgram? Program { get; private set; }
    public JobControlPhase Phase { get; private set; }
    public bool IsBusy => Phase != JobControlPhase.None;
    public UploadProgress? Upload { get; private set; }
    public DownloadProgress? Download { get; private set; }

    /// <summary>Why the last request failed, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>The programs folder on the machine, as last listed.</summary>
    public IReadOnlyList<RemoteEntry>? MachineFiles { get; private set; }
    public bool ListingFiles { get; private set; }

    /// <summary>Job options this machine offers; empty while its type (travel) is unknown.</summary>
    public IReadOnlyList<JobMode> Modes => _session.Limits.Travel is { } travel ? MachineTypes.JobModesFor(travel) : [];

    public static bool CanStartIn(StatusReport? status) => MachineSession.IsIdle(status) && status?.IsPlaying != true;

    /// <summary>Why a program can't be started right now, or null when it can.</summary>
    public string? WhyNotStart => _startRules.FirstOrDefault(r => r.Blocks(this)) is { Reason: { } reason } ? reason(this) : null;

    public bool CanPause => _session.Status?.IsPlaying == true && JobSuspension.HasState(_session.Status, "Run");
    public bool CanResume => JobSuspension.IsSuspended(_session.Status) && !_session.BedCleaning.IsRunning;
    public bool CanStop => _session.Status?.IsPlaying == true && !_session.BedCleaning.IsRunning;

    /// <summary>Forgets the chosen program; the next start needs a new choice. Nothing changes while a job runs.</summary>
    public void Clear()
    {
        if (IsBusy) throw new InvalidOperationException("Busy with the previous request.");
        Program = null;
        Setup = null;
        SetupFile = null;
        ForgetRestart();
        LastError = null;
        _tracker.ClearProgram();
        _session.NotifyChanged();
    }

    /// <summary>Chooses a file from this computer.</summary>
    public void LoadLocal(string fileName, byte[] content)
    {
        var name = Path.GetFileName(fileName);
        Use(new LocalProgram(name, content), content, _cache.Add(name, content), setup: null, file: null);
    }

    /// <summary>Chooses a file on the machine; it is downloaded (or taken from the cache) to analyse it.</summary>
    public async Task LoadFromMachineAsync(string machinePath)
    {
        await RunExclusiveAsync(JobControlPhase.Loading, async () =>
        {
            var progress = new Progress<DownloadProgress>(p => { Download = p; _session.NotifyChanged(); });
            var result = await _session.DownloadAsync(machinePath, progress, _cache);
            Use(new MachineProgram(machinePath), result.Content, _cache.Add(RemotePath.FileName(machinePath), result.Content), setup: null, file: null);
        });
        Download = null;
    }

    /// <summary>Lists the machine's programs folder, newest first.</summary>
    public async Task RefreshFilesAsync()
    {
        ListingFiles = true;
        _session.NotifyChanged();
        try
        {
            var entries = await _session.ListFilesAsync(RemotePath.GCodeDirectory);
            MachineFiles = entries.Where(e => !e.IsDirectory).OrderByDescending(e => e.Modified).ThenBy(e => e.Name).ToList();
        }
        finally
        {
            ListingFiles = false;
            _session.NotifyChanged();
        }
    }

    /// <summary>The commands a start would send now.</summary>
    /// <exception cref="JobStartException">The options don't fit the program or the machine.</exception>
    public JobStartPlan Plan()
    {
        var program = Program ?? throw new JobStartException("Choose a program first.");
        var travel = _session.Limits.Travel ?? throw new JobStartException("The machine's travel limits are not known yet.");
        var status = _session.Status;
        var context = new JobStartContext(
            travel, program.Bounds, status?.MachinePosition, status?.Tool?.Active, Modes, program.SelectsTool);
        return JobStartPlan.Create(Options, context, program.Source.MachinePath, WaitForGo);
    }

    /// <summary>Uploads the program if needed and starts it; a failure ends up in <see cref="LastError"/>.</summary>
    public async Task StartAsync()
    {
        if (WhyNotStart is { } reason)
        {
            LastError = reason;
            _session.NotifyChanged();
            return;
        }
        var program = Program!;
        var (setup, file) = (Setup, SetupFile);
        await RunExclusiveAsync(JobControlPhase.Uploading, async () =>
        {
            var plan = Plan();
            _store.Save(_session.Address, Options);
            var progress = new Progress<UploadProgress>(p => { Upload = p; _session.NotifyChanged(); });
            await program.Source.PutOnMachineAsync(_session, progress);
            SetPhase(JobControlPhase.Starting);
            await RunAsync(plan, program);
            if (Options.Timelapse) Timelapse.Begin(program.Source.FileName, _tracker.Timeline?.TotalSeconds ?? 0);
            if (setup is not null) CountStart(setup, file);
        });
        Upload = null;
    }

    /// <summary>Switches the auto Z probe; switched on, it probes in the middle of the program's area.</summary>
    public void SetZProbe(bool on)
    {
        Options.ZProbe = on;
        if (on) CenterZProbe();
        _session.NotifyChanged();
    }

    /// <summary>Puts the Z probing point in the middle of the program's area, counted from its corner.</summary>
    public void CenterZProbe()
    {
        if (Program?.Bounds is { } bounds) Options.ZProbeAtCenterOf(bounds);
        _session.NotifyChanged();
    }

    /// <summary>Changes what the Z probe offset counts from, keeping the point.</summary>
    public void SetZProbeFromWorkOrigin(bool fromWorkOrigin)
    {
        Options.SetZProbeFromWorkOrigin(fromWorkOrigin, Program?.Bounds);
        _session.NotifyChanged();
    }

    /// <summary>Pauses the program (<c>suspend</c>): the queued moves finish first.</summary>
    public Task PauseAsync() => _session.SendAsync(JobSuspension.SuspendCommand);

    /// <summary>Continues a paused program: the head goes back to where it paused first.</summary>
    public Task ResumeAsync() => _session.SendAsync(JobSuspension.ResumeCommand);

    /// <summary>Ends the program: the spindle stops and the remaining moves are dropped.</summary>
    public Task StopAsync()
    {
        StopRequestedUtc = DateTime.UtcNow;
        return _session.SendAsync(JobCommands.Abort);
    }

    /// <summary>Continues a job that waits for the go (<see cref="WaitForGo"/>) or is paused.</summary>
    public Task ContinueAsync() => ResumeAsync();

    /// <param name="setup">The setup it runs with: its options are used as saved. Any other program gets the Z probe
    /// in its middle, as an offset from the previous program's corner means nothing for it.</param>
    /// <param name="file">The setup's file it is, when it is one.</param>
    private void Use(IProgramSource source, byte[] content, GCodeAnalysis analysis, Setup? setup, SetupFile? file)
    {
        Program = new SelectedProgram(source, content, analysis);
        Setup = setup;
        SetupFile = file;
        ForgetRestart();
        if (setup is null && Options.ZProbe && !Options.ZProbeFromWorkOrigin && Program.Bounds is { } bounds) Options.ZProbeAtCenterOf(bounds);
        LastError = null;
        Show();
        _session.NotifyChanged();
    }

    /// <summary>The chosen program goes on the job's timeline, unless a job is running.</summary>
    private void Show()
    {
        if (_tracker.IsPlaying || Program is not { } program) return;
        _tracker.ShowProgram(program.Source.FileName, program.Analysis, () => Program is null ? null : Plan().Preparation);
    }

    private async Task RunAsync(JobStartPlan plan, SelectedProgram program)
    {
        var replies = new ConcurrentQueue<string>();
        void Collect(string text) => replies.Enqueue(text.Trim());
        _session.TextReceived += Collect;
        _tracker.ExpectPreparation(plan.Preparation);
        var playing = false;
        try
        {
            _session.Note($"Starting {program.Source.FileName} ({program.Source.Describe()})");
            foreach (var command in plan.Commands) await _session.SendAsync(command);
            var status = await _session.WaitForStatusAsync(s => s.IsPlaying || JobSuspension.HasState(s, "Alarm"), StartTimeout);
            // Not homed: the machine refuses with an alarm, which also drops the queued commands.
            if (!status.IsPlaying) throw new InvalidOperationException($"The machine raised an alarm instead of starting: {_session.LastAlarm}");
            playing = true;
        }
        catch (TimeoutException)
        {
            var answer = replies.LastOrDefault(r => r.Length > 0 && r != "ok" && !r.StartsWith("Command buffered", StringComparison.Ordinal));
            throw new InvalidOperationException(
                "The machine did not start the program" + (answer is null ? "" : $" (its last answer: \"{answer}\")") +
                ". The commands queued for the start (probing, tool) stay queued until a program starts or the " +
                "machine is reset, so reset it before trying again.");
        }
        finally
        {
            _session.TextReceived -= Collect;
            if (!playing) _tracker.ExpectPreparation(null); // a later job started elsewhere is not this one
        }
    }

    private async Task RunExclusiveAsync(JobControlPhase phase, Func<Task> action)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            LastError = "Busy with the previous request.";
            _session.NotifyChanged();
            return;
        }
        LastError = null;
        SetPhase(phase);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _session.Note($"Job: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            SetPhase(JobControlPhase.None);
        }
    }

    private void SetPhase(JobControlPhase phase)
    {
        Phase = phase;
        _session.NotifyChanged();
    }
}

/// <summary>One <see cref="JobControl"/> per machine session.</summary>
public sealed class JobControls(JobTrackers trackers, GCodeCache cache, JobOptionsStore store, SetupLibrary library, CameraService camera)
{
    private readonly ConcurrentDictionary<string, JobControl> _controls = new(StringComparer.OrdinalIgnoreCase);

    public JobControl Get(MachineSession session) =>
        _controls.GetOrAdd(session.Address, _ => new JobControl(session, trackers.Get(session), cache, store, library, camera));
}
