using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using SpawnWear.Drivers.SdCard;

namespace SpawnWear.Services
{
    /// <summary>
    /// Lightweight description of one installed app on the SD card.
    /// </summary>
    public class AppInfo
    {
        /// <summary>Logical name = the .pe filename without its extension (e.g. "HelloWorld").</summary>
        public string Name;
        /// <summary>Full path on the mounted volume (e.g. "D:\apps\HelloWorld.pe").</summary>
        public string FullPath;
        /// <summary>Size of the .pe file in bytes.</summary>
        public long Size;
    }

    /// <summary>
    /// The watch's installed-app library. Persists uploaded SpawnWear apps
    /// (.pe assemblies) to the SD card under D:\apps\ so they survive a reboot,
    /// and exposes list / install / read / uninstall plus a "last launched"
    /// pointer the boot path uses to re-activate the most recent app.
    ///
    /// This is the firmware half of the Companion app-manager: the HTTP server's
    /// /apps routes drive these methods, and Program.Main reads LastApp at boot.
    ///
    /// SD access only works once SdCardService has mounted D:\ (SDSPI + exFAT,
    /// see SdCardService). Every method is defensive - a missing card, a bad
    /// file, or a runtime FATFS hiccup surfaces as a false/null return + a
    /// Debug line, never an unhandled exception that takes down the watch.
    /// </summary>
    public class AppRepositoryService
    {
        public const string AppsDir = "D:\\apps";
        // Logical name of the last app the user launched. Persisted as plain
        // text so a reboot can re-activate it. Lives inside AppsDir but does not
        // end in .pe, so List() never mistakes it for an app.
        const string LastAppFile = "D:\\apps\\_lastapp.txt";
        const string PeExtension = ".pe";
        const int MaxNameLength = 40;

        readonly SdCardService _sd;
        bool _ready;

        public AppRepositoryService(SdCardService sd)
        {
            _sd = sd;
        }

        /// <summary>True once the apps directory exists on a mounted card.</summary>
        public bool IsReady => _ready;

        /// <summary>
        /// Ensures D:\apps\ exists on the mounted SD card. Returns false (and the
        /// repository stays not-ready) if the card isn't mounted or the directory
        /// can't be created - callers should treat a not-ready repo as "no apps".
        /// </summary>
        public bool Initialize()
        {
            if (_sd == null || !_sd.IsMounted)
            {
                Debug.WriteLine("[AppRepo] SD not mounted - app library unavailable");
                _ready = false;
                return false;
            }
            try
            {
                if (!Directory.Exists(AppsDir))
                {
                    Directory.CreateDirectory(AppsDir);
                    Debug.WriteLine("[AppRepo] created " + AppsDir);
                }
                _ready = true;
                Debug.WriteLine("[AppRepo] ready at " + AppsDir);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] Initialize EX " + ex.GetType().Name + ": " + ex.Message);
                _ready = false;
                return false;
            }
        }

        /// <summary>
        /// Returns the installed apps (name + size), sorted by directory order.
        /// Empty array if the repo isn't ready or the directory is empty.
        /// </summary>
        public AppInfo[] ListInfo()
        {
            if (!_ready) return new AppInfo[0];
            try
            {
                string[] files = Directory.GetFiles(AppsDir);
                ArrayList list = new ArrayList();
                for (int i = 0; i < files.Length; i++)
                {
                    string full = files[i];
                    if (!HasPeExtension(full)) continue;
                    var info = new AppInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(full),
                        FullPath = full,
                        Size = SafeSize(full),
                    };
                    list.Add(info);
                }
                var result = new AppInfo[list.Count];
                for (int i = 0; i < list.Count; i++) result[i] = (AppInfo)list[i];
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] ListInfo EX " + ex.GetType().Name + ": " + ex.Message);
                return new AppInfo[0];
            }
        }

        /// <summary>True if an app with this logical name is installed.</summary>
        public bool Exists(string name)
        {
            string clean = SanitizeName(name);
            if (clean == null) return false;
            try { return File.Exists(PathFor(clean)); }
            catch { return false; }
        }

        /// <summary>
        /// Writes a .pe assembly to D:\apps\&lt;name&gt;.pe, overwriting any prior
        /// install of the same name. Returns false on a bad name, an empty
        /// payload, or an I/O failure.
        /// </summary>
        public bool Install(string name, byte[] peBytes)
        {
            if (!_ready) { Debug.WriteLine("[AppRepo] Install: repo not ready"); return false; }
            string clean = SanitizeName(name);
            if (clean == null) { Debug.WriteLine("[AppRepo] Install: invalid name '" + name + "'"); return false; }
            if (peBytes == null || peBytes.Length == 0) { Debug.WriteLine("[AppRepo] Install: empty payload"); return false; }
            try
            {
                string path = PathFor(clean);
                // WriteAllBytes truncates/overwrites, so a reinstall replaces cleanly.
                File.WriteAllBytes(path, peBytes);
                Debug.WriteLine("[AppRepo] installed " + clean + " (" + peBytes.Length + " bytes) -> " + path);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] Install EX " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reads an installed app's bytes, or null if it doesn't exist / can't
        /// be read. Caller (HttpServer) feeds these to Assembly.Load.
        /// </summary>
        public byte[] Read(string name)
        {
            if (!_ready) return null;
            string clean = SanitizeName(name);
            if (clean == null) return null;
            try
            {
                string path = PathFor(clean);
                if (!File.Exists(path)) { Debug.WriteLine("[AppRepo] Read: not found " + path); return null; }
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] Read EX " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Deletes an installed app. Clears the LastApp pointer if it referenced
        /// the removed app. Returns false on a bad name or I/O failure; returns
        /// true if the app was already absent (idempotent uninstall).
        /// </summary>
        public bool Uninstall(string name)
        {
            if (!_ready) return false;
            string clean = SanitizeName(name);
            if (clean == null) return false;
            try
            {
                string path = PathFor(clean);
                if (File.Exists(path)) File.Delete(path);
                if (LastApp == clean) LastApp = null;
                Debug.WriteLine("[AppRepo] uninstalled " + clean);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] Uninstall EX " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The logical name of the last app the user launched, persisted to the
        /// SD card so the boot path can re-activate it. Null when none is set.
        /// Setting to null removes the pointer file.
        /// </summary>
        public string LastApp
        {
            get
            {
                if (!_ready) return null;
                try
                {
                    if (!File.Exists(LastAppFile)) return null;
                    string raw = File.ReadAllText(LastAppFile);
                    string clean = SanitizeName(raw == null ? null : raw.Trim());
                    return clean;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AppRepo] LastApp get EX " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
            }
            set
            {
                if (!_ready) return;
                try
                {
                    if (value == null)
                    {
                        if (File.Exists(LastAppFile)) File.Delete(LastAppFile);
                        return;
                    }
                    string clean = SanitizeName(value);
                    if (clean == null) return;
                    File.WriteAllText(LastAppFile, clean);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AppRepo] LastApp set EX " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ----- helpers -----

        static string PathFor(string cleanName)
        {
            return AppsDir + "\\" + cleanName + PeExtension;
        }

        static long SafeSize(string fullPath)
        {
            try { return new FileInfo(fullPath).Length; }
            catch { return 0; }
        }

        static bool HasPeExtension(string fullPath)
        {
            string ext = Path.GetExtension(fullPath);
            return ext != null && ext.ToLower() == PeExtension;
        }

        /// <summary>
        /// Reduces a caller-supplied name to a safe SD filename stem: letters,
        /// digits, '_', '-' and spaces only; everything else is dropped, the
        /// .pe extension is stripped if present, and the result is trimmed and
        /// length-capped. Returns null if nothing usable remains, which guards
        /// against path traversal and invalid-filename FATFS errors.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (name == null) return null;
            // Drop a trailing .pe so callers can pass either "Foo" or "Foo.pe".
            if (name.Length > PeExtension.Length)
            {
                string tail = name.Substring(name.Length - PeExtension.Length);
                if (tail.ToLower() == PeExtension) name = name.Substring(0, name.Length - PeExtension.Length);
            }
            char[] chars = name.ToCharArray();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                          || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == ' ';
                if (ok) sb.Append(c);
                if (sb.Length >= MaxNameLength) break;
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
    }
}
