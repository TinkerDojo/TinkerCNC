using Microsoft.Data.Sqlite;
using TinkerCNC.Programs;

namespace TinkerCNC.Host;

/// <summary>Saved programs (<see cref="SavedProgramLibrary"/>): loading one, saving the chosen program and its options.</summary>
public sealed partial class JobControl
{
    /// <summary>The saved program the chosen program was loaded from or saved as, as it is stored; null otherwise.</summary>
    public SavedProgram? Saved { get; private set; }

    /// <summary>Whether the options differ from the loaded saved program's.</summary>
    public bool SavedChanged => Saved is { } saved && !OptionColumns.Same(saved.Options, Options);

    /// <summary>Chooses a saved program with its options exactly as saved.</summary>
    public void LoadSaved(long id)
    {
        RequireIdle();
        var saved = _library.Get(id);
        var content = _library.ReadFile(saved);
        Options = saved.Options.Clone();
        Use(SourceOf(saved, content), content, _cache.Add(saved.OriginalFileName, content), saved);
    }

    /// <summary>
    /// Saves the chosen program and the options under a new name. Under the loaded saved program's own name it updates
    /// that one instead.
    /// </summary>
    /// <param name="replace">Whether another saved program of that name is replaced (it is deleted and can be restored).</param>
    /// <exception cref="SavedProgramNameTakenException">The name is taken and <paramref name="replace"/> is false.</exception>
    public void SaveAs(string name, string notes, bool replace)
    {
        RequireIdle();
        var program = Program ?? throw new SavedProgramException("Choose a program first.");
        if (Saved is { } current && string.Equals(current.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _library.UpdateOptions(current.Id, Options);
            Saved = _library.Edit(current.Id, name, notes);
        }
        else
        {
            Saved = _library.Save(name, notes, program.Source.FileName, program.Content, Options, replace);
        }
        _session.NotifyChanged();
    }

    /// <summary>Stores the changed options in the loaded saved program.</summary>
    public void SaveChanges()
    {
        RequireIdle();
        var saved = Saved ?? throw new SavedProgramException("No saved program is loaded.");
        Saved = _library.UpdateOptions(saved.Id, Options);
        _session.NotifyChanged();
    }

    private static LocalProgram SourceOf(SavedProgram saved, byte[] content) =>
        new(saved.OriginalFileName, content, $"saved program \"{saved.Name}\"");

    /// <summary>Follows changes made in the list (here or in another window) to the loaded saved program.</summary>
    private void OnLibraryChanged()
    {
        if (Saved is not { } loaded) return;
        var fresh = _library.Find(loaded.Id);
        if (fresh is null || fresh.IsDeleted)
            Saved = null;
        else if (fresh.StoredFile != loaded.StoredFile && !IsBusy && TryRead(fresh) is { } content)
            Use(SourceOf(fresh, content), content, _cache.Add(fresh.OriginalFileName, content), fresh); // new file, same options
        else
            Saved = fresh;
        _session.NotifyChanged();
    }

    private byte[]? TryRead(SavedProgram program)
    {
        try { return _library.ReadFile(program); }
        catch (SavedProgramException) { return null; }
    }

    /// <summary>The job is running whatever happens here: a failure to count it is only noted.</summary>
    private void CountStart(SavedProgram saved)
    {
        try { _library.RecordStart(saved.Id); }
        catch (SqliteException ex) { _session.Note($"Could not count the start of \"{saved.Name}\": {ex.Message}"); }
    }

    private void RequireIdle()
    {
        if (IsBusy) throw new SavedProgramException("Busy with the previous request.");
    }
}
