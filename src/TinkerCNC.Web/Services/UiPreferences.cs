using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace TinkerCNC.Web.Services;

/// <summary>
/// Per-browser UI settings (collapsed panels and the like) in protected local storage. Storage can be missing
/// (private mode) or unreadable (tampered value); then the fallback is used and changes simply are not remembered.
/// Only usable once the component has rendered (it needs JS interop).
/// </summary>
public sealed class UiPreferences(ProtectedLocalStorage storage)
{
    public async Task<T> GetAsync<T>(string key, T fallback)
    {
        try
        {
            var stored = await storage.GetAsync<T>(key);
            return stored.Success && stored.Value is { } value ? value : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        try { await storage.SetAsync(key, value!); }
        catch (Exception) { /* not remembered, still applied */ }
    }
}
