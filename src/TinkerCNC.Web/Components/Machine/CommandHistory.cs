namespace TinkerCNC.Web.Components.Machine;

/// <summary>Shell-like history of console commands, navigated with the arrow keys.</summary>
public sealed class CommandHistory
{
    private static readonly IReadOnlyDictionary<string, int> KeySteps = new Dictionary<string, int>
    {
        ["ArrowUp"] = -1,
        ["ArrowDown"] = +1,
    };

    private readonly List<string> _entries = new();
    private int _index;

    public void Add(string command)
    {
        _entries.Add(command);
        _index = _entries.Count;
    }

    /// <summary>Moves through the history for a navigation key; returns null for any other key.</summary>
    public string? Navigate(string key)
    {
        if (_entries.Count == 0 || !KeySteps.TryGetValue(key, out var step)) return null;
        _index = Math.Clamp(_index + step, 0, _entries.Count);
        return _index < _entries.Count ? _entries[_index] : "";
    }
}
