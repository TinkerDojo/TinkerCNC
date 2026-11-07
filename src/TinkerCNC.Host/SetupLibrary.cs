using TinkerCNC.Jobs;
using TinkerCNC.Programs;

namespace TinkerCNC.Host;

/// <summary>A setup's file as listed, with where its copy is and whether it is still there.</summary>
public sealed record SetupFileEntry(SetupFile File, string FilePath, bool FileMissing);

/// <summary>A setup as listed, with its files.</summary>
public sealed record SetupEntry(Setup Setup, IReadOnlyList<SetupFileEntry> Files);

/// <summary>A setup or one of its files can't be changed that way.</summary>
public class SetupException(string message) : InvalidOperationException(message);

/// <summary>Another setup (not deleted) already has the name.</summary>
public sealed class SetupNameTakenException(string name) : SetupException($"A setup called \"{name}\" already exists.")
{
    public string Name { get; } = name;
}

/// <summary>
/// Setups and their files, shared by all machines: the rows in the database (<see cref="SetupRepository"/>) and the
/// G-code copies in the setup files folder (<see cref="AppPaths.SetupFiles"/>), named by <see cref="StoredFiles"/>.
/// Setup names are unique (ignoring case) among setups that are not deleted. Deleting a setup or a file keeps the
/// rows and the copies so they can be restored; only deleting for good removes them.
/// </summary>
public sealed class SetupLibrary
{
    public const int MaxNameLength = 120;

    private readonly SetupRepository _repository;
    private readonly Lock _lock = new();

    public SetupLibrary(SetupRepository repository, AppPaths paths)
    {
        _repository = repository;
        Directory = paths.SetupFiles;
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    /// <summary>Raised after any change, from the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>The setups with their files; deleted setups and deleted files only when asked for.</summary>
    public IReadOnlyList<SetupEntry> List(bool includeDeleted)
    {
        var files = _repository.ListFiles(includeDeleted).ToLookup(f => f.SetupId);
        return _repository.ListSetups(includeDeleted).Select(s => new SetupEntry(s, files[s.Id].Select(Entry).ToList())).ToList();
    }

    public Setup Get(long id) => Find(id) ?? throw new SetupException("That setup no longer exists.");

    /// <summary>The setup, deleted or not; null once deleted for good.</summary>
    public Setup? Find(long id) => _repository.GetSetup(id);

    public SetupFile GetFile(long id) => FindFile(id) ?? throw new SetupException("That file no longer exists.");

    public SetupFile? FindFile(long id) => _repository.GetFile(id);

    /// <summary>The files of one setup, deleted ones only when asked for.</summary>
    public IReadOnlyList<SetupFileEntry> FilesOf(long setupId, bool includeDeleted) =>
        _repository.ListFiles(includeDeleted).Where(f => f.SetupId == setupId).Select(Entry).ToList();

    public string PathOf(SetupFile file) => Path.Combine(Directory, file.StoredFile);

    /// <exception cref="SetupException">Its copy is gone.</exception>
    public byte[] ReadFile(SetupFile file)
    {
        var path = PathOf(file);
        return File.Exists(path)
            ? File.ReadAllBytes(path)
            : throw new SetupException($"The file \"{file.OriginalFileName}\" is missing ({path}). Replace it or delete it.");
    }

    /// <summary>Creates a setup with these options and no file yet.</summary>
    /// <param name="replace">Whether a setup of that name is deleted to make room; otherwise the name must be free.</param>
    /// <exception cref="SetupNameTakenException">The name is taken and <paramref name="replace"/> is false.</exception>
    public Setup Create(string name, string notes, JobStartOptions options, bool replace)
    {
        name = ValidName(name);
        var id = Change(() =>
        {
            if (FindActive(name) is { } existing)
            {
                if (!replace) throw new SetupNameTakenException(existing.Name);
                _repository.SetSetupDeleted(existing.Id, existing.Name, DateTime.UtcNow);
            }
            return _repository.InsertSetup(name, notes.Trim(), options, DateTime.UtcNow);
        });
        return Get(id);
    }

    /// <summary>Adds a file to a setup that is not deleted, with its own copy of <paramref name="content"/>.</summary>
    public SetupFile AddFile(long setupId, string originalFileName, byte[] content)
    {
        var id = Change(() =>
        {
            Active(setupId);
            var stored = StoredFiles.Store(Directory, originalFileName, content);
            try
            {
                return _repository.InsertFile(setupId, stored, StoredFiles.FileNameOf(originalFileName), Md5(content), DateTime.UtcNow);
            }
            catch
            {
                File.Delete(Path.Combine(Directory, stored));
                throw;
            }
        });
        return GetFile(id);
    }

    /// <summary>Replaces the options of a setup that is not deleted.</summary>
    public Setup UpdateOptions(long id, JobStartOptions options)
    {
        Change(() =>
        {
            Active(id);
            _repository.UpdateOptions(id, options, DateTime.UtcNow);
        });
        return Get(id);
    }

    /// <summary>Renames it and replaces its notes.</summary>
    /// <exception cref="SetupNameTakenException">Another setup has that name.</exception>
    public Setup Edit(long id, string name, string notes)
    {
        name = ValidName(name);
        Change(() =>
        {
            var setup = Active(id);
            RequireFree(name, setup.Id);
            _repository.UpdateDetails(id, name, notes.Trim(), DateTime.UtcNow);
        });
        return Get(id);
    }

    /// <summary>
    /// Gives a file a new G-code copy. The previous copy is deleted once the new one is stored: nothing refers to it
    /// any more.
    /// </summary>
    public SetupFile ReplaceFile(long fileId, string originalFileName, byte[] content)
    {
        Change(() =>
        {
            var file = ActiveFile(fileId);
            var stored = StoredFiles.Store(Directory, originalFileName, content);
            _repository.UpdateFile(fileId, stored, StoredFiles.FileNameOf(originalFileName), Md5(content), DateTime.UtcNow);
            File.Delete(PathOf(file));
        });
        return GetFile(fileId);
    }

    /// <summary>Hides the setup and its files and frees its name; everything stays so it can be restored.</summary>
    public void Delete(long id) => Change(() =>
    {
        var setup = Active(id);
        _repository.SetSetupDeleted(id, setup.Name, DateTime.UtcNow);
    });

    /// <summary>Brings a deleted setup back, under <paramref name="newName"/> when given.</summary>
    /// <exception cref="SetupNameTakenException">Its name was taken meanwhile; restore it under another one.</exception>
    public Setup Restore(long id, string? newName = null)
    {
        Change(() =>
        {
            var setup = Get(id);
            var name = newName is null ? setup.Name : ValidName(newName);
            RequireFree(name, id);
            _repository.SetSetupDeleted(id, name, null);
        });
        return Get(id);
    }

    /// <summary>Removes a deleted setup with all its files and their copies for good.</summary>
    public void DeleteForever(long id) => Change(() =>
    {
        var setup = Get(id);
        if (!setup.IsDeleted) throw new SetupException("Only deleted setups can be deleted for good.");
        var files = _repository.ListFiles(includeDeleted: true).Where(f => f.SetupId == id).ToList();
        _repository.RemoveSetup(id);
        foreach (var file in files) File.Delete(PathOf(file));
    });

    /// <summary>Hides a file; its row and copy stay so it can be restored.</summary>
    public void DeleteFile(long fileId) => Change(() =>
    {
        ActiveFile(fileId);
        _repository.SetFileDeleted(fileId, DateTime.UtcNow);
    });

    public SetupFile RestoreFile(long fileId)
    {
        Change(() =>
        {
            var file = GetFile(fileId);
            Active(file.SetupId);
            _repository.SetFileDeleted(fileId, null);
        });
        return GetFile(fileId);
    }

    /// <summary>Removes a deleted file and its copy for good.</summary>
    public void DeleteFileForever(long fileId) => Change(() =>
    {
        var file = GetFile(fileId);
        if (!file.IsDeleted) throw new SetupException("Only deleted files can be deleted for good.");
        _repository.RemoveFile(fileId);
        File.Delete(PathOf(file));
    });

    /// <summary>Counts a start of the setup and of the file (null for a file that is not the setup's own).</summary>
    public void RecordStart(long setupId, long? fileId) => Change(() =>
    {
        var now = DateTime.UtcNow;
        _repository.RecordSetupStart(setupId, now);
        if (fileId is { } id) _repository.RecordFileStart(id, now);
    });

    public static string ValidName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new SetupException("Give the setup a name.");
        if (trimmed.Length > MaxNameLength) throw new SetupException($"Names can have at most {MaxNameLength} characters.");
        return trimmed;
    }

    private SetupFileEntry Entry(SetupFile file)
    {
        var path = PathOf(file);
        return new SetupFileEntry(file, path, !File.Exists(path));
    }

    private Setup? FindActive(string name) =>
        _repository.ListSetups(includeDeleted: false).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private void RequireFree(string name, long id)
    {
        if (FindActive(name) is { } other && other.Id != id) throw new SetupNameTakenException(other.Name);
    }

    private Setup Active(long id)
    {
        var setup = Get(id);
        return setup.IsDeleted ? throw new SetupException($"\"{setup.Name}\" was deleted; restore it first.") : setup;
    }

    private SetupFile ActiveFile(long id)
    {
        var file = GetFile(id);
        if (file.IsDeleted) throw new SetupException($"\"{file.OriginalFileName}\" was deleted; restore it first.");
        Active(file.SetupId);
        return file;
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
