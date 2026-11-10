using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace TinkerCNC.Web.Services;

/// <summary>What the browser allows for system notifications.</summary>
public enum NotificationPermission
{
    /// <summary>No Notification API here, or the page is not secure (plain http to another computer).</summary>
    Unsupported,

    /// <summary>Not asked yet.</summary>
    Default,

    Granted,

    /// <summary>Blocked; only the browser's site settings can allow it again.</summary>
    Denied,
}

/// <summary>What became of a notification handed to the browser.</summary>
public enum NotificationOutcome
{
    /// <summary>The browser reported showing it (the system may still hide it, e.g. in Do Not Disturb).</summary>
    Shown,

    /// <summary>Taken, but never reported as shown: notifications of the browser are likely off in the system's settings.</summary>
    Silent,

    /// <summary>The browser could not show it.</summary>
    Failed,

    /// <summary>Not allowed (any more).</summary>
    Blocked,
}

/// <summary>A notification for the system, already in the dashboard's language.</summary>
/// <param name="Tag">Identifies it across tabs (the dashboard notification's id).</param>
/// <param name="Url">Dashboard page a click opens.</param>
/// <param name="Sticky">Stays until clicked or closed (it waits for an answer).</param>
public sealed record SystemNotification(string Tag, string Title, string Body, string Url, bool Sticky);

/// <summary>
/// The system notifications of one browser tab (<c>wwwroot/js/system-notifications.js</c>): whether they are switched
/// on (remembered in this browser, Settings → Notifications), what the browser allows, and showing them. Usable once
/// the component has rendered (it needs JS interop).
/// </summary>
public sealed class SystemNotifications(IJSRuntime js, UiPreferences preferences) : IAsyncDisposable
{
    private const string EnabledKey = "notifications.system";

    private Task<IJSObjectReference>? _module;
    private bool? _enabled;

    /// <summary>Switched on or off in this tab (other tabs read the setting when they load).</summary>
    public event Action? Changed;

    public async Task<bool> IsEnabledAsync() => _enabled ??= await preferences.GetAsync(EnabledKey, false);

    public async Task SetEnabledAsync(bool enabled)
    {
        _enabled = enabled;
        await preferences.SetAsync(EnabledKey, enabled);
        Changed?.Invoke();
    }

    public async Task<NotificationPermission> PermissionAsync() => Parse(await CallAsync<string>("permission"));

    /// <summary>A permission as the browser names it ("granted", "denied", "default", "unsupported").</summary>
    public static NotificationPermission Parse(string value) =>
        Enum.TryParse<NotificationPermission>(value, ignoreCase: true, out var permission) ? permission : NotificationPermission.Unsupported;

    /// <summary>
    /// Lets a checkbox switch them on: ticking it asks for permission in the browser itself (the only place the prompt
    /// is allowed to appear), then calls <c>ToggledAsync(bool wanted, string permission, bool refusedWithoutAsking)</c> on <paramref name="callback"/>.
    /// </summary>
    public async Task WatchToggleAsync<TCallback>(ElementReference checkbox, DotNetObjectReference<TCallback> callback)
        where TCallback : class
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("watchToggle", checkbox, callback);
    }

    public async Task<NotificationOutcome> ShowAsync(SystemNotification notification) =>
        Enum.TryParse<NotificationOutcome>(
            await CallAsync<string>("show", notification.Tag, notification.Title, notification.Body, notification.Url, notification.Sticky),
            ignoreCase: true, out var outcome)
            ? outcome
            : NotificationOutcome.Failed;

    public async Task CloseAsync(string tag)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("close", tag);
    }

    private async Task<T> CallAsync<T>(string function, params object?[] args)
    {
        var module = await ModuleAsync();
        return await module.InvokeAsync<T>(function, args);
    }

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/system-notifications.js").AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await (await _module).DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
