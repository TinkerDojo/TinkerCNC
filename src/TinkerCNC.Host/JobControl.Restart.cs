using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Jobs;

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

    /// <summary>The operations of the whole program a start can begin at (the chosen program's, trimmed or not).</summary>
    public IReadOnlyList<ToolSegment> Operations => (_wholeProgram ?? Program)?.Analysis.Segments ?? [];

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
        var whole = _wholeProgram ?? Program ?? throw new InvalidOperationException("Choose a program first.");
        if (segmentIndex is not { } index || index <= 0)
        {
            Program = whole;
            ForgetRestart();
        }
        else
        {
            if (index >= whole.Analysis.Segments.Count) throw new InvalidOperationException("The program has no such operation.");
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
        var run = _tracker.LastRun ?? throw new InvalidOperationException("No job has ended yet.");
        var analysis = run.Analysis ?? throw new InvalidOperationException("The last job's program is not known.");
        var content = Encoding.UTF8.GetBytes(string.Join('\n', analysis.Lines) + "\n");
        var name = run.FileName ?? "program.nc";
        IProgramSource source = run.Path is { } path && path.StartsWith('/') ? new MachineProgram(path) : new LocalProgram(name, content, "the last job");
        Use(source, content, analysis, setup: null, file: null);
    }

    private void ForgetRestart()
    {
        _wholeProgram = null;
        StartSegment = null;
    }
}
