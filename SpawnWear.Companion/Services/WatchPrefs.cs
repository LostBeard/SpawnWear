using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;

namespace SpawnWear.Companion.Services;

/// <summary>
/// Small wrapper around <c>window.localStorage</c> for things the user
/// sets in the PWA that should survive page refreshes - the watch's
/// HTTP URL today, more later (preferred BLE device id once we wire
/// remembered pairing, log retention size, etc.).
///
/// Persists per-origin (so a Companion served from
/// <c>https://spawnwear.example.com</c> sees a different store than
/// one served from <c>http://localhost:5251</c>). Cleared by the user
/// via the browser's site-data tools.
/// </summary>
public class WatchPrefs
{
    const string KeyWatchUrl = "spawnwear.watchUrl";

    readonly BlazorJSRuntime _js;
    public WatchPrefs(BlazorJSRuntime js) { _js = js; }

    public string? WatchUrl
    {
        get
        {
            try
            {
                using var window = _js.Get<Window>("window");
                using var ls = window.LocalStorage;
                return ls?.GetItem(KeyWatchUrl);
            }
            catch { return null; }
        }
        set
        {
            try
            {
                using var window = _js.Get<Window>("window");
                using var ls = window.LocalStorage;
                if (ls is null) return;
                if (string.IsNullOrEmpty(value)) ls.RemoveItem(KeyWatchUrl);
                else ls.SetItem(KeyWatchUrl, value);
            }
            catch { /* no localStorage (private mode, sandboxed iframe) - silently skip */ }
        }
    }
}
