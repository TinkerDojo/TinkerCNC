namespace TinkerCNC.Web.Components;

/// <summary>
/// A destructive action that needs a second click within <see cref="Window"/>. Tracks which action (by key) is waiting
/// for it; await <see cref="AskAsync"/> from the click handler so the button turns back once the window has passed.
/// </summary>
public sealed class ConfirmClick
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private object? _key;
    private DateTime _until;

    public bool IsAsking(object key) => Equals(_key, key) && DateTime.UtcNow < _until;

    public Task AskAsync(object key)
    {
        _key = key;
        _until = DateTime.UtcNow + Window;
        return Task.Delay(Window);
    }

    public void Clear() => _key = null;
}
