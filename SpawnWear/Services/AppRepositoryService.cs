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
        /// <summary>App id = the app directory name (e.g. "Counter").</summary>
        public string Name;
        /// <summary>Full path to the entry assembly (e.g. "D:\apps\Counter\app.pe").</summary>
        public string FullPath;
        /// <summary>Size of the entry .pe in bytes.</summary>
        public long Size;
    }

    /// <summary>
    /// The watch's installed-app library. Each app is a DIRECTORY of loose files
    /// on the SD card under D:\apps\&lt;id&gt;\ - an entry assembly "app.pe", a
    /// "manifest.json", an icon, and any assets the app reads at runtime. The
    /// directory IS the package: it is what a multi-file torrent shares and what a
    /// BEP 46 update pointer refers to, and apps/devs/users can read+write the
    /// files directly. (Pre-dir installs - a flat D:\apps\&lt;X&gt;.pe - are
    /// auto-migrated to D:\apps\&lt;X&gt;\app.pe on Initialize so nothing is lost.)
    ///
    /// The watch uses the layout by CONVENTION (entry = app.pe), so it needs no
    /// JSON parser yet; manifest.json carries the richer metadata (display name,
    /// version, icon, permissions, BEP 46 update key) for the Companion / dev /
    /// torrent side, and the watch will start reading it once a parser is added.
    ///
    /// SD access only works once SdCardService has mounted D:\. Every method is
    /// defensive - a missing card, a bad file, or a FATFS hiccup surfaces as a
    /// false/null return + a Debug line, never an unhandled exception.
    /// </summary>
    public class AppRepositoryService
    {
        public const string AppsDir = "D:\\apps";
        // Logical name of the last app the user launched. Persisted as plain text so
        // a reboot can re-activate it. A FILE in AppsDir (not a subdir), so the
        // subdir-based app enumeration never mistakes it for an app.
        const string LastAppFile = "D:\\apps\\_lastapp.txt";
        const string EntryPe = "app.pe";           // entry assembly filename, by convention
        const string ManifestFile = "manifest.json";
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
        /// Ensures D:\apps\ exists on the mounted SD card, then migrates any
        /// pre-dir flat apps. Returns false (and the repo stays not-ready) if the
        /// card isn't mounted or the directory can't be created.
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
                MigrateFlatApps();
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
        /// One-time migration: a pre-dir flat install "D:\apps\&lt;X&gt;.pe" becomes the
        /// directory app "D:\apps\&lt;X&gt;\app.pe" (+ a minimal manifest). Idempotent:
        /// skips an id whose directory already exists.
        /// </summary>
        void MigrateFlatApps()
        {
            try
            {
                string[] files = Directory.GetFiles(EnsureTrailing(AppsDir));
                for (int i = 0; i < files.Length; i++)
                {
                    string full = files[i];
                    if (!HasPeExtension(full)) continue;
                    string id = SanitizeName(Path.GetFileNameWithoutExtension(full));
                    if (id == null) continue;
                    string dir = AppsDir + "\\" + id;
                    if (Directory.Exists(dir)) continue;
                    Directory.CreateDirectory(dir);
                    File.Move(full, dir + "\\" + EntryPe);
                    WriteMinimalManifest(id, dir);
                    Debug.WriteLine("[AppRepo] migrated flat app -> " + dir);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] MigrateFlatApps EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Returns the installed apps (id + entry size). An app is any subdirectory
        /// of D:\apps\ that contains an "app.pe". Empty if the repo isn't ready.
        /// </summary>
        public AppInfo[] ListInfo()
        {
            if (!_ready) return new AppInfo[0];
            try
            {
                string[] dirs = Directory.GetDirectories(EnsureTrailing(AppsDir));
                ArrayList list = new ArrayList();
                for (int i = 0; i < dirs.Length; i++)
                {
                    string id = Path.GetFileName(dirs[i]);
                    string entry = AppsDir + "\\" + id + "\\" + EntryPe;
                    if (!File.Exists(entry)) continue; // not a valid app dir
                    list.Add(new AppInfo { Name = id, FullPath = entry, Size = SafeSize(entry) });
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

        /// <summary>True if an app with this id is installed (its app.pe exists).</summary>
        public bool Exists(string id)
        {
            string clean = SanitizeName(id);
            if (clean == null) return false;
            try { return File.Exists(EntryPath(clean)); }
            catch { return false; }
        }

        /// <summary>
        /// Convenience single-assembly install: creates D:\apps\&lt;id&gt;\ with app.pe
        /// (+ a minimal manifest if none exists). A full packaged install (icon,
        /// assets, a real manifest) is done file-by-file over sys.files by the
        /// Companion; this is the quick path for a bare app. Returns false on a bad
        /// id, empty payload, or I/O failure.
        /// </summary>
        public bool Install(string id, byte[] peBytes)
        {
            if (!_ready) { Debug.WriteLine("[AppRepo] Install: repo not ready"); return false; }
            string clean = SanitizeName(id);
            if (clean == null) { Debug.WriteLine("[AppRepo] Install: invalid id '" + id + "'"); return false; }
            if (peBytes == null || peBytes.Length == 0) { Debug.WriteLine("[AppRepo] Install: empty payload"); return false; }
            try
            {
                string dir = AppsDir + "\\" + clean;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(EntryPath(clean), peBytes);
                WriteMinimalManifest(clean, dir);
                Debug.WriteLine("[AppRepo] installed " + clean + " (" + peBytes.Length + " bytes) -> " + dir);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] Install EX " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reads an installed app's entry assembly bytes, or null if it doesn't
        /// exist / can't be read. Caller feeds these to Assembly.Load.
        /// </summary>
        public byte[] Read(string id)
        {
            if (!_ready) return null;
            string clean = SanitizeName(id);
            if (clean == null) return null;
            try
            {
                string path = EntryPath(clean);
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
        /// Deletes an installed app (its whole directory). Clears the LastApp
        /// pointer if it referenced the removed app. Idempotent.
        /// </summary>
        public bool Uninstall(string id)
        {
            if (!_ready) return false;
            string clean = SanitizeName(id);
            if (clean == null) return false;
            try
            {
                string dir = AppsDir + "\\" + clean;
                if (Directory.Exists(dir)) DeleteDirRecursive(dir);
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
        /// The id of the last app the user launched, persisted to the SD card so the
        /// boot path can re-activate it. Null when none is set.
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
                    return SanitizeName(raw == null ? null : raw.Trim());
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

        static string EntryPath(string id)
        {
            return AppsDir + "\\" + id + "\\" + EntryPe;
        }

        // Writes a minimal schema-conformant manifest if the app dir has none, so a
        // bare install still produces a valid package. Never clobbers a real manifest
        // (e.g. one the Companion pushed). The watch doesn't parse this yet.
        static void WriteMinimalManifest(string id, string dir)
        {
            try
            {
                string mf = dir + "\\" + ManifestFile;
                if (File.Exists(mf)) return;
                string json = "{\"id\":\"" + id + "\",\"name\":\"" + id +
                              "\",\"version\":\"1.0.0\",\"entry\":\"" + EntryPe +
                              "\",\"icon\":\"icon.png\"}";
                File.WriteAllText(mf, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppRepo] WriteMinimalManifest EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void DeleteDirRecursive(string dir)
        {
            string ep = EnsureTrailing(dir); // nanoFramework FATFS enumeration needs the trailing backslash
            string[] files = Directory.GetFiles(ep);
            for (int i = 0; i < files.Length; i++) File.Delete(files[i]);
            string[] subs = Directory.GetDirectories(ep);
            for (int i = 0; i < subs.Length; i++) DeleteDirRecursive(subs[i]);
            Directory.Delete(dir);
        }

        // nanoFramework's FATFS GetFiles/GetDirectories return nothing for a non-root
        // path without a trailing backslash (the drive root "D:\" already has one).
        static string EnsureTrailing(string p)
        {
            return (p.Length > 0 && p[p.Length - 1] == '\\') ? p : p + "\\";
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
        /// Reduces a caller-supplied id to a safe SD directory name: letters, digits,
        /// '_', '-' and spaces only; a trailing .pe is stripped; trimmed and
        /// length-capped. Returns null if nothing usable remains, guarding against
        /// path traversal and invalid-filename FATFS errors.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (name == null) return null;
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
