using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Jobs;
using TinkerCNC.Recovery;

namespace TinkerCNC.Host;

/// <summary>
/// Running a job again, also from one of its tool operations (<see cref="ProgramRestart"/>): after a job ended early,
/// the chosen program is trimmed to begin at that operation, with probing off and the work origin kept, since the
/// piece has not moved. The whole program stays at hand to go back to.
/// </summary>
public sealed partial class JobControl
{
    private SelectedProgram? _wholeProgram; // the program before it was trimmed for a restart

    /// <summary>The tool operation the chosen program begins at, when it is a restart; null for the whole file.</summary>
    public ToolSegment? StartSegment { get; private set; }

    /// <summary>The stopped job the chosen program resumes (<see cref="LoadResume"/>); null otherwise.</summary>
    public JobSnapshot? Resuming { get; private set; }

    /// <summary>What the resume copy does before the program's own lines (<see cref="ProgramResume"/>).</summary>
    public IReadOnlyList<string> ResumePreamble { get; private set; } = [];

    /// <summary>The operations of the whole program a start can begin at (the chosen program's, trimmed or not).</summary>
    public IReadOnlyList<ToolSegment> Operations => (_wholeProgram ?? Program)?.Analysis.Segments ?? [];

    /// <summary>The chosen program before it was trimmed for a restart, or the chosen program itself.</summary>
    public SelectedProgram? WholeProgram => _wholeProgram ?? Program;

    /// <summary>The file the chosen program was trimmed from, or the chosen file itself.</summary>
    public string? WholeFileName => (_wholeProgram ?? Program)?.Source.FileName;

    /// <summary>
    /// Begins the chosen program at the operation with that index (null or 0: the whole file). Beginning later
    /// switches scan margin, Z probe and leveling off and keeps the work origin; the options can be changed again
    /// afterwards.
    /// </summary>
    public void StartFrom(int? segmentIndex)
    {
        RequireIdle();
        var whole = _wholeProgram ?? Program ?? throw new LocalizedException(HostTexts.Of("Program.ChooseFirst"));
        if (segmentIndex is not { } index || index <= 0)
        {
            Program = whole;
            ForgetRestart();
        }
        else
        {
            Resuming = null;
            if (index >= whole.Analysis.Segments.Count) throw new LocalizedException(HostTexts.Of("Restart.NoSuchOperation"));
            var segment = whole.Analysis.Segments[index];
            var restart = ProgramRestart.From(whole.Analysis, whole.Source.FileName, segment);
            Program = new SelectedProgram(
                new LocalProgram(restart.FileName, restart.Content, restart.Description), restart.Content,
                _cache.Add(restart.FileName, restart.Content));
            _wholeProgram = whole;
            StartSegment = segment;
            Options.ScanMargin = Options.ZProbe = Options.Leveling = false;
            Options.WorkOrigin = WorkOriginChoice.Keep;
        }
        LastError = null;
        Show();
        _session.NotifyChanged();
    }

    /// <summary>
    /// Chooses the program of the job that ended last again, with the options as they are: as the file on the
    /// machine when the machine named it, otherwise as a copy to upload.
    /// </summary>
    public void LoadLastRun()
    {
        RequireIdle();
        var run = _tracker.LastRun ?? throw new LocalizedException(HostTexts.Of("Restart.NoLastJob"));
        var analysis = run.Analysis ?? throw new LocalizedException(HostTexts.Of("Restart.LastProgramUnknown"));
        var content = Encoding.UTF8.GetBytes(string.Join('\n', analysis.Lines) + "\n");
        var name = run.FileName ?? "program.nc";
        IProgramSource source = run.Path is { } path && path.StartsWith('/') ? new MachineProgram(path) : new LocalProgram(name, content, HostTexts.Of("Program.LastJob"));
        Use(source, content, analysis, setup: null, file: null);
    }

    /// <summary>
    /// Chooses a stopped job again, to resume it at the line where it was last seen (<see cref="JobRecovery"/>): a copy
    /// that brings the machine back there first (<see cref="ProgramResume"/>). Probing and leveling are off and the
    /// work origin is the one the job had; the whole program stays at hand for further runs (the queue's repeats).
    /// </summary>
    /// <exception cref="LocalizedException">The program is no longer in the cache, or the machine's travel is unknown.</exception>
    public void LoadResume(JobSnapshot snapshot, GCodeAnalysis program)
    {
        RequireIdle();
        var travel = _session.Limits.Travel ?? throw new LocalizedException(_session.Limits.Error ?? HostTexts.Of("Limits.Unknown"));
        var content = Encoding.UTF8.GetBytes(string.Join('\n', program.Lines)); // the lines end with an empty one when the file ended with a line break
        var resume = ProgramResume.From(program, snapshot.FileName, snapshot.Line, new ResumeContext(travel.ClearanceZ, snapshot.WorkZero));
        Use(new LocalProgram(snapshot.FileName, content, HostTexts.Of("Program.Stopped")), content, program, setup: null, file: null);
        _wholeProgram = Program;
        Program = new SelectedProgram(new LocalProgram(resume.FileName, resume.Content, resume.Description), resume.Content,
            _cache.Add(resume.FileName, resume.Content));
        Resuming = snapshot;
        ResumePreamble = resume.Preamble ?? [];
        Options.ScanMargin = Options.ZProbe = Options.Leveling = false;
        Options.WorkOrigin = WorkOriginChoice.Keep;
        Show();
        _session.NotifyChanged();
    }

    private void ForgetRestart()
    {
        _wholeProgram = null;
        StartSegment = null;
        Resuming = null;
        ResumePreamble = [];
    }
}
