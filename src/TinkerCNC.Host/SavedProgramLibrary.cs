using TinkerCNC.Jobs;
using TinkerCNC.Programs;

namespace TinkerCNC.Host;

/// <summary>A saved program as listed, with where its file is and whether it is still there.</summary>
public sealed record SavedProgramEntry(SavedProgram Program, string FilePath, bool FileMissing);

/// <summary>A saved program can't be changed that way.</summary>
public class SavedProgramException(string message) : InvalidOperationException(message);

/// <summary>Another saved program (not deleted) already has the name.</summary>
public sealed class SavedProgramNameTakenException(string name)
    : SavedProgramException($"A saved program called \"{name}\" already exists.")
{
    public string Name { get; } = name;
}

/// <summary>
/// Saved programs, shared by all machines: the rows in the database (<see cref="SavedProgramRepository"/>) and their
/// G-code files in the saved programs folder (<see cref="AppPaths.SavedPrograms"/>). Each saved program has its own
/// copy, named after the original file with a GUID before the extension, so files of the same name can be saved for
/// different programs. Names are unique (ignoring case) among programs that are not deleted. Deleting keeps the row
/// and the file so it can be restored; only deleting for good removes both.
/// </summary>
public sealed class SavedProgramLibrary
{
    public const int MaxNameLength = 120;

    /// <summary>Not allowed in file names on any system the app runs on.</summary>
    private static readonly char[] InvalidFileNameChars = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly SavedProgramRepository _repository;
    private readonly Lock _lock = new();

    public SavedProgramLibrary(SavedProgramRepository repository, AppPaths paths)
    {
        _repository = repository;
        Directory = paths.SavedPrograms;
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    /// <summary>Raised after any change, from the thread that made it.</summary>
    public event Action? Changed;

    public IReadOnlyList<SavedProgramEntry> List(bool includeDeleted) =>
        _repository.List(includeDeleted).Select(Entry).ToList();

    public SavedProgram Get(long id) => Find(id) ?? throw new SavedProgramException("That saved program no longer exists.");

    /// <summary>The program, deleted or not; null once deleted for good.</summary>
    public SavedProgram? Find(long id) => _repository.Get(id);

    public string PathOf(SavedProgram program) => Path.Combine(Directory, program.StoredFile);

    /// <exception cref="SavedProgramException">Its file is gone.</exception>
    public byte[] ReadFile(SavedProgram program)
    {
        var path = PathOf(program);
        return File.Exists(path)
            ? File.ReadAllBytes(path)
            : throw new SavedProgramException($"The file of \"{program.Name}\" is missing ({path}). Replace its file or delete it.");
    }

    /// <summary>Saves a new program with its own copy of <paramref name="content"/>.</summary>
    /// <param name="replace">Whether a program of that name is deleted to make room; otherwise the name must be free.</param>
    /// <exception cref="SavedProgramNameTakenException">The name is taken and <paramref name="replace"/> is false.</exception>
    public SavedProgram Save(string name, string notes, string originalFileName, byte[] content, JobStartOptions options, bool replace)
    {
        name = ValidName(name);
        var id = Change(() =>
        {
            if (FindActive(name) is { } existing)
            {
                if (!replace) throw new SavedProgramNameTakenException(existing.Name);
                _repository.SetDeleted(existing.Id, existing.Name, DateTime.UtcNow);
            }
            var stored = Store(originalFileName, content);
            try
            {
                return _repository.Insert(name, notes.Trim(), stored, FileNameOf(originalFileName), Md5(content), options, DateTime.UtcNow);
            }
            catch
            {
                File.Delete(Path.Combine(Directory, stored));
                throw;
            }
        });
        return Get(id);
    }

    /// <summary>Replaces the options of a saved program that is not deleted.</summary>
    public SavedProgram UpdateOptions(long id, JobStartOptions options)
    {
        Change(() =>
        {
            Active(id);
            _repository.UpdateOptions(id, options, DateTime.UtcNow);
        });
        return Get(id);
    }

    /// <summary>Renames it and replaces its notes.</summary>
    /// <exception cref="SavedProgramNameTakenException">Another program has that name.</exception>
    public SavedProgram Edit(long id, string name, string notes)
    {
        name = ValidName(name);
        Change(() =>
        {
            var program = Active(id);
            RequireFree(name, program.Id);
            _repository.UpdateDetails(id, name, notes.Trim(), DateTime.UtcNow);
        });
        return Get(id);
    }

    /// <summary>
    /// Gives it a new G-code file and keeps its options. The previous copy is deleted once the new one is stored:
    /// nothing refers to it any more.
    /// </summary>
    public SavedProgram ReplaceFile(long id, string originalFileName, byte[] content)
    {
        Change(() =>
        {
            var program = Active(id);
            var stored = Store(originalFileName, content);
            _repository.UpdateFile(id, stored, FileNameOf(originalFileName), Md5(content), DateTime.UtcNow);
            File.Delete(PathOf(program));
        });
        return Get(id);
    }

    /// <summary>Hides it from the list and frees its name; its row and file stay so it can be restored.</summary>
    public void Delete(long id) => Change(() =>
    {
        var program = Active(id);
        _repository.SetDeleted(id, program.Name, DateTime.UtcNow);
    });

    /// <summary>Brings a deleted program back, under <paramref name="newName"/> when given.</summary>
    /// <exception cref="SavedProgramNameTakenException">Its name was taken meanwhile; restore it under another one.</exception>
    public SavedProgram Restore(long id, string? newName = null)
    {
        Change(() =>
        {
            var program = Get(id);
            var name = newName is null ? program.Name : ValidName(newName);
            RequireFree(name, id);
            _repository.SetDeleted(id, name, null);
        });
        return Get(id);
    }

    /// <summary>Removes a deleted program and its file for good.</summary>
    public void DeleteForever(long id) => Change(() =>
    {
        var program = Get(id);
        if (!program.IsDeleted) throw new SavedProgramException("Only deleted programs can be deleted for good.");
        _repository.Remove(id);
        File.Delete(PathOf(program));
    });

    /// <summary>Counts a start: the machine is playing it.</summary>
    public void RecordStart(long id) => Change(() => _repository.RecordStart(id, DateTime.UtcNow));

    public static string ValidName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new SavedProgramException("Give the program a name.");
        if (trimmed.Length > MaxNameLength) throw new SavedProgramException($"Names can have at most {MaxNameLength} characters.");
        return trimmed;
    }

    /// <summary>
    /// The copy's file name: the original name made safe, with a GUID before the extension. Directories are stripped
    /// and the name split by hand, because <see cref="Path"/> treats ':' as a separator on Windows only and the
    /// result must be the same on every system.
    /// </summary>
    public static string StoredFileName(string originalFileName)
    {
        var safe = new string(FileNameOf(originalFileName).Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray());
        var dot = safe.LastIndexOf('.');
        var (stem, extension) = dot < 0 ? (safe, "") : (safe[..dot], safe[dot..]);
        if (extension == ".") extension = ""; // a trailing dot is dropped by Windows file systems
        return $"{(stem.Length > 0 ? stem : "program")}_{Guid.NewGuid():N}{extension}";
    }

    /// <summary>The part after the last '/' or '\\', whatever system the path came from.</summary>
    public static string FileNameOf(string path) => path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

    private SavedProgramEntry Entry(SavedProgram program)
    {
        var path = PathOf(program);
        return new SavedProgramEntry(program, path, !File.Exists(path));
    }

    private string Store(string originalFileName, byte[] content)
    {
        var stored = StoredFileName(originalFileName);
        File.WriteAllBytes(Path.Combine(Directory, stored), content);
        return stored;
    }

    private SavedProgram? FindActive(string name) =>
        _repository.List(includeDeleted: false).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private void RequireFree(string name, long id)
    {
        if (FindActive(name) is { } other && other.Id != id) throw new SavedProgramNameTakenException(other.Name);
    }

    private SavedProgram Active(long id)
    {
        var program = Get(id);
        return program.IsDeleted ? throw new SavedProgramException($"\"{program.Name}\" was deleted; restore it first.") : program;
    }

    private static string Md5(byte[] content) => GCodeCache.Md5Of(content);

    private void Change(Action change) => Change(() =>
    {
        change();
        return 0;
    });

    private T Change<T>(Func<T> change)
    {
        T result;
        lock (_lock) result = change();
        Changed?.Invoke();
        return result;
    }
}
