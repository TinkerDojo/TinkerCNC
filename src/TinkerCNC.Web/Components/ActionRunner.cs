using TinkerCNC.Localization;

namespace TinkerCNC.Web.Components;

/// <summary>Runs a UI action and keeps its error (as a text for people) for display instead of throwing into the circuit.</summary>
public sealed class ActionRunner
{
    public Text? Error { get; private set; }

    public async Task RunAsync(Func<Task> action)
    {
        Error = null;
        try { await action(); }
        catch (Exception ex) { Error = Text.Of(ex); }
    }

    public void Run(Action action) => _ = RunAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    public void Clear() => Error = null;
}
