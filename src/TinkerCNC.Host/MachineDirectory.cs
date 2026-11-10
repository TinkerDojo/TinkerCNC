using System.Collections.Concurrent;
using System.Text.Json;
using TinkerCNC.Discovery;
using TinkerCNC.Transport;

namespace TinkerCNC.Host;

/// <summary>One machine as shown in the list, merged from every source.</summary>
public sealed class MachineEntry
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    /// <summary>Connection address "ip" or "ip:port".</summary>
    public string? Address { get; set; }
    public bool Manual { get; set; }
    public bool Discovered => LastSeenUtc is not null;
    public DateTime? LastSeenUtc { get; set; }
    public bool Busy { get; set; }
    public string? Extra { get; set; }

    public bool Online => LastSeenUtc is { } t && DateTime.UtcNow - t < MachineDirectory.OnlineWindow;
    public string? Host => Address is null ? null : TcpTransport.Parse(Address).Host;
}

/// <summary>A machine added by hand, as persisted in <c>machines.json</c>.</summary>
public sealed record ManualMachine(string Name, string Address);

/// <summary>
/// Combines two sources of machines:
/// <list type="bullet">
/// <item>live UDP discovery on port 3333 (see <see cref="DiscoveryService"/>),</item>
/// <item>machines added by hand in this app (stored in <c>machines.json</c> in the data folder).</item>
/// </list>
/// Entries are matched by machine name, falling back to the address.
/// </summary>
public sealed class MachineDirectory
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, MachineEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _manualPath;
    private readonly ILogger<MachineDirectory> _log;
    private readonly object _fileLock = new();

    public MachineDirectory(AppPaths paths, ILogger<MachineDirectory> log)
    {
        _log = log;
        _manualPath = paths.ManualMachines;
        AdoptLegacyFile(paths.LegacyManualMachines);
        LoadManual();
    }

    public event Action? Changed;

    public DiscoveryStatus Discovery { get; } = new();

    public IReadOnlyList<MachineEntry> All => _entries.Values
        .OrderByDescending(e => e.Online)
        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public MachineEntry? Find(string keyOrAddress) =>
        _entries.TryGetValue(keyOrAddress, out var e)
            ? e
            : _entries.Values.FirstOrDefault(x => string.Equals(x.Address, keyOrAddress, StringComparison.OrdinalIgnoreCase));

    public void ReportDiscovered(DiscoveredMachine m)
    {
        var e = Upsert(m.Name, m.ConnectAddress, preferNewAddress: true);
        e.LastSeenUtc = DateTime.UtcNow;
        e.Busy = m.Busy;
        e.Extra = m.Extra;
        Changed?.Invoke();
    }

    public MachineEntry AddManual(string address, string? name)
    {
        TcpTransport.Parse(address); // validate
        var e = Upsert(string.IsNullOrWhiteSpace(name) ? address : name.Trim(), address.Trim(), preferNewAddress: true);
        e.Manual = true;
        SaveManual();
        Changed?.Invoke();
        return e;
    }

    /// <summary>Drops a manual entry. A machine that announces itself stays listed as discovered.</summary>
    public void Remove(MachineEntry entry)
    {
        if (entry.Discovered) entry.Manual = false;
        else _entries.TryRemove(entry.Key, out _);
        SaveManual();
        Changed?.Invoke();
    }

    private MachineEntry Upsert(string name, string? address, bool preferNewAddress = false)
    {
        var key = string.IsNullOrWhiteSpace(name) ? address ?? Guid.NewGuid().ToString() : name;
        var entry = _entries.GetOrAdd(key, k => new MachineEntry { Key = k, Name = name });

        // A machine first added by IP may later announce itself by name: merge the two.
        if (address is not null && !string.Equals(key, address, StringComparison.OrdinalIgnoreCase)
            && _entries.TryGetValue(address, out var byAddress) && !ReferenceEquals(byAddress, entry))
        {
            entry.Manual |= byAddress.Manual;
            _entries.TryRemove(address, out _);
            if (entry.Manual) SaveManual();
        }

        if (address is not null && (preferNewAddress || string.IsNullOrEmpty(entry.Address)))
            entry.Address = address;
        return entry;
    }

    /// <summary>Earlier versions kept the manual machines next to the app; take them over once.</summary>
    private void AdoptLegacyFile(string legacyPath)
    {
        if (File.Exists(_manualPath) || !File.Exists(legacyPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manualPath)!);
            File.Copy(legacyPath, _manualPath);
            _log.LogInformation("Moved manual machines from {Old} to {New}", legacyPath, _manualPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not copy {Path}", legacyPath);
        }
    }

    private void LoadManual()
    {
        try
        {
            if (!File.Exists(_manualPath)) return;
            var list = JsonSerializer.Deserialize<List<ManualMachine>>(File.ReadAllText(_manualPath)) ?? new();
            foreach (var m in list)
                Upsert(m.Name, m.Address).Manual = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Path}", _manualPath);
        }
    }

    private void SaveManual()
    {
        lock (_fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manualPath)!);
            var list = _entries.Values.Where(e => e.Manual && e.Address is not null)
                .Select(e => new ManualMachine(e.Name, e.Address!)).ToList();
            File.WriteAllText(_manualPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

public sealed class DiscoveryStatus
{
    public bool Listening { get; set; }

    /// <summary>Why listening failed, or null.</summary>
    public Text? Error { get; set; }
    public DateTime? LastPacketUtc { get; set; }
    public long Packets { get; set; }
}
