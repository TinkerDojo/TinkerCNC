using Microsoft.Data.Sqlite;
using TinkerCNC.Jobs;
using TinkerCNC.Programs;

namespace TinkerCNC.Host;

/// <summary>Setups (<see cref="SetupLibrary"/>): loading one of a setup's files, saving the chosen program and its options.</summary>
public sealed partial class JobControl
{
    /// <summary>The setup whose options the chosen program was loaded with or saved to, as it is stored; null otherwise.</summary>
    public Setup? Setup { get; private set; }

    /// <summary>The setup's file the chosen program is, when it is one; null for any other file.</summary>
    public SetupFile? SetupFile { get; private set; }

    /// <summary>Whether the options differ from the loaded setup's.</summary>
    public bool SetupChanged => Setup is { } setup && !OptionColumns.Same(setup.Options, Options);

    /// <summary>Chooses one of a setup's files, with the setup's options exactly as saved.</summary>
    public void LoadSetupFile(long fileId)
    {
        RequireIdle();
        var file = _library.GetFile(fileId);
        var setup = _library.Get(file.SetupId);
        var content = _library.ReadFile(file);
        Options = setup.Options.Clone();
        Use(SourceOf(setup, file.OriginalFileName, content), content, _cache.Add(file.OriginalFileName, content), setup, file);
    }

    /// <summary>
    /// Adds the chosen program to a setup (unless it is that setup's file already) and, when asked, stores the
    /// options as they are now in it. The program is that setup's afterwards.
    /// </summary>
    public void SaveToSetup(long setupId, bool updateOptions)
    {
        RequireIdle();
        var program = Program ?? throw new SetupException(HostTexts.Of("Program.ChooseFirst"));
        if (updateOptions) _library.UpdateOptions(setupId, Options);
        var file = SetupFile?.SetupId == setupId ? SetupFile : _library.AddFile(setupId, program.Source.FileName, program.Content);
        Adopt(_library.Get(setupId), file, program);
    }

    /// <summary>Creates a setup with the options as they are now and the chosen program as its first file.</summary>
    /// <param name="replace">Whether another setup of that name is replaced (it is deleted and can be restored).</param>
    /// <exception cref="SetupNameTakenException">The name is taken and <paramref name="replace"/> is false.</exception>
    public void SaveAsNewSetup(string name, string notes, bool replace)
    {
        RequireIdle();
        var program = Program ?? throw new SetupException(HostTexts.Of("Program.ChooseFirst"));
        var setup = _library.Create(name, notes, Options, replace);
        var file = _library.AddFile(setup.Id, program.Source.FileName, program.Content);
        Adopt(setup, file, program);
    }

    /// <summary>Stores the changed options in the loaded setup.</summary>
    public void SaveChanges()
    {
        RequireIdle();
        var setup = Setup ?? throw new SetupException(HostTexts.Of("Setup.NoneLoaded"));
        Setup = _library.UpdateOptions(setup.Id, Options);
        _session.NotifyChanged();
    }

    private void Adopt(Setup setup, SetupFile file, SelectedProgram program)
    {
        Setup = setup;
        SetupFile = file;
        Program = program with { Source = SourceOf(setup, file.OriginalFileName, program.Content) };
        _session.NotifyChanged();
    }

    /// <summary>Chooses a file for the queue: its options, the probe point kept inside the file's area, a restart if asked.</summary>
    /// <param name="setup">The setup it belongs to, if any (its starts are counted).</param>
    /// <param name="file">The setup's file it is, or null for a copy queued for its runs only.</param>
    /// <param name="options">The options it runs with: the setup's, or the entry's own.</param>
    /// <param name="restartSegment">Index of the tool operation to begin at, after a failure.</param>
    public void LoadQueued(Setup? setup, SetupFile? file, string fileName, byte[] content, JobStartOptions options, int? restartSegment)
    {
        RequireIdle();
        var source = setup is null
            ? new LocalProgram(fileName, content, HostTexts.Of("Program.Queued"))
            : new LocalProgram(fileName, content, HostTexts.Of("Program.FromSetupQueued", setup.Name));
        Use(source, content, _cache.Add(fileName, content), setup, file);
        Options = options.Clone(); // exactly these: no Z probe point moved for a file without a setup
        if (Options.ZProbe && Program!.Bounds is { } bounds && !Options.ZProbeWithin(bounds))
        {
            Options.ZProbeAtCenterOf(bounds);
            _session.Note($"Queue: the Z probe point lies outside the area of {fileName}; it is probed in the middle of that area instead.");
        }
        if (restartSegment is { } index) StartFrom(index);
    }

    private static LocalProgram SourceOf(Setup setup, string fileName, byte[] content) =>
        new(fileName, content, HostTexts.Of("Program.FromSetup", setup.Name));

    /// <summary>Follows changes made in the library (here or in another window) to the loaded setup and file.</summary>
    private void OnLibraryChanged()
    {
        if (Setup is not { } loaded) return;
        var fresh = _library.Find(loaded.Id);
        if (fresh is null || fresh.IsDeleted)
        {
            Setup = null;
            SetupFile = null;
        }
        else
        {
            Setup = fresh;
            FollowFile(fresh);
        }
        _session.NotifyChanged();
    }

    private void FollowFile(Setup setup)
    {
        if (SetupFile is not { } loaded) return;
        var fresh = _library.FindFile(loaded.Id);
        if (fresh is null || fresh.IsDeleted)
            SetupFile = null;
        else if (fresh.StoredFile != loaded.StoredFile && !IsBusy && TryRead(fresh) is { } content)
            Use(SourceOf(setup, fresh.OriginalFileName, content), content, _cache.Add(fresh.OriginalFileName, content), setup, fresh); // new file, same options
        else
            SetupFile = fresh;
    }

    private byte[]? TryRead(SetupFile file)
    {
        try { return _library.ReadFile(file); }
        catch (SetupException) { return null; }
    }

    /// <summary>The job is running whatever happens here: a failure to count it is only noted.</summary>
    private void CountStart(Setup setup, SetupFile? file)
    {
        try { _library.RecordStart(setup.Id, file?.Id); }
        catch (SqliteException ex) { _session.Note($"Could not count the start of \"{setup.Name}\": {ex.Message}"); }
    }

    private void RequireIdle()
    {
        if (IsBusy) throw new SetupException(HostTexts.Of("Job.Busy"));
    }
}
