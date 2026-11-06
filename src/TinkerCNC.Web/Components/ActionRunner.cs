namespace TinkerCNC.Web.Components;

/// <summary>Runs a UI action and keeps its error message for display instead of throwing into the circuit.</summary>
public sealed class ActionRunner
{
    public string? Error { get; private set; }

    public async Task RunAsync(Func<Task> action)
    {
        Error = null;
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    public void Run(Action action) => _ = RunAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    public void Clear() => Error = null;
}
