using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinkerCNC.Jobs;

namespace TinkerCNC.Host;

/// <summary>The options last used to start a program on each machine, kept in a JSON file in the data folder.</summary>
public sealed class JobOptionsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JobOptionsStore> _log;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, JobStartOptions> _byMachine;

    public JobOptionsStore(AppPaths paths, ILogger<JobOptionsStore> log)
    {
        _path = paths.JobOptions;
        _log = log;
        _byMachine = new ConcurrentDictionary<string, JobStartOptions>(Load(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A copy of the saved options, or the defaults.</summary>
    public JobStartOptions Get(string machine) => _byMachine.TryGetValue(machine, out var options) ? options.Clone() : new JobStartOptions();

    public void Save(string machine, JobStartOptions options)
    {
        _byMachine[machine] = options.Clone();
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new SortedDictionary<string, JobStartOptions>(_byMachine), Json));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the job options to {Path}", _path);
        }
    }

    private Dictionary<string, JobStartOptions> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, JobStartOptions>>(File.ReadAllText(_path), Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the job options from {Path}", _path);
            return new();
        }
    }
}
