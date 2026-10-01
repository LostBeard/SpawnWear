using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using SpawnWear.Drivers.Rtc;

namespace SpawnWear.Services
{
    /// <summary>
    /// Wall-clock time for the watch: the PCF85063 RTC keeps UTC, this service turns it into local
    /// time for the user's time zone (built-in table with DST rules, chosen in Settings and persisted
    /// on internal flash), and keeps the RTC right by syncing from a TRUSTED source on WiFi.
    ///
    /// Trusted sync: the time written to the RTC only ever comes from the Date header of an HTTPS
    /// response whose TLS certificate verified against a pinned root (GTS Root R1 for www.google.com,
    /// GTS Root R4 for www.cloudflare.com - embedded, not read from the SD card, so swapping a file
    /// can't change what is trusted). Plain NTP is unauthenticated and is not used.
    ///
    /// Bootstrap: mbedTLS checks certificate validity dates against the system clock, so a badly wrong
    /// clock fails verification. The system clock is set from the RTC at boot; if verification still
    /// fails, one UNVERIFIED fetch sets the system clock only (never the RTC) and the verified fetch is
    /// retried - the RTC is written only from a verified response.
    ///
    /// Sync runs on its own thread: after WiFi is up, then every 6 hours (15 minutes after a failure).
    /// </summary>
    public class TimeService
    {
        // ----- Time zones: id, display name, standard offset (minutes east of UTC), DST rule -----
        const byte DstNone = 0, DstUs = 1, DstEu = 2, DstAu = 3, DstNz = 4;

        static readonly string[] ZoneIds =
        {
            "utc", "london", "c-europe", "e-europe", "moscow", "dubai", "india", "china", "japan",
            "sydney", "brisbane", "auckland", "hawaii", "alaska", "pacific", "mountain", "arizona",
            "central", "eastern", "atlantic", "brazil",
        };
        static readonly string[] ZoneNames =
        {
            "UTC", "LONDON", "C EUROPE", "E EUROPE", "MOSCOW", "DUBAI", "INDIA", "CHINA", "JAPAN",
            "SYDNEY", "BRISBANE", "AUCKLAND", "HAWAII", "ALASKA", "PACIFIC", "MOUNTAIN", "ARIZONA",
            "CENTRAL", "EASTERN", "ATLANTIC", "BRAZIL",
        };
        static readonly int[] ZoneStdMinutes =
        {
            0, 0, 60, 120, 180, 240, 330, 480, 540,
            600, 600, 720, -600, -540, -480, -420, -420,
            -360, -300, -240, -180,
        };
        static readonly byte[] ZoneDst =
        {
            DstNone, DstEu, DstEu, DstEu, DstNone, DstNone, DstNone, DstNone, DstNone,
            DstAu, DstNone, DstNz, DstNone, DstUs, DstUs, DstUs, DstNone,
            DstUs, DstUs, DstUs, DstNone,
        };
        const string DefaultZoneId = "eastern";
        const string ZoneFile = "I:\\timezone.txt";

        public static int ZoneCount { get { return ZoneIds.Length; } }
        public static string ZoneName(int i) { return ZoneNames[i]; }

        /// <summary>"UTC-5" style label of a zone's standard offset (DST adds an hour where it applies).</summary>
        public static string ZoneOffsetLabel(int i)
        {
            int m = ZoneStdMinutes[i];
            if (m == 0) return "UTC";
            string s = "UTC" + (m < 0 ? "-" : "+");
            if (m < 0) m = -m;
            s += (m / 60).ToString();
            if (m % 60 != 0) s += ":" + (m % 60).ToString();
            return s;
        }

        // ----- Trusted sources -----
        static readonly string[] SourceHosts = { "www.google.com", "www.cloudflare.com" };
        // Pinned roots (Google Trust Services, both valid to 2036-06-22). Verified 2026-10-01: each
        // root's public key matches the root in the chain the live host serves.
        const string GtsRootR1 =
            "-----BEGIN CERTIFICATE-----\n" +
            "MIIFVzCCAz+gAwIBAgINAgPlk28xsBNJiGuiFzANBgkqhkiG9w0BAQwFADBHMQsw\n" +
            "CQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEU\n" +
            "MBIGA1UEAxMLR1RTIFJvb3QgUjEwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAw\n" +
            "MDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZp\n" +
            "Y2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjEwggIiMA0GCSqGSIb3DQEBAQUA\n" +
            "A4ICDwAwggIKAoICAQC2EQKLHuOhd5s73L+UPreVp0A8of2C+X0yBoJx9vaMf/vo\n" +
            "27xqLpeXo4xL+Sv2sfnOhB2x+cWX3u+58qPpvBKJXqeqUqv4IyfLpLGcY9vXmX7w\n" +
            "Cl7raKb0xlpHDU0QM+NOsROjyBhsS+z8CZDfnWQpJSMHobTSPS5g4M/SCYe7zUjw\n" +
            "TcLCeoiKu7rPWRnWr4+wB7CeMfGCwcDfLqZtbBkOtdh+JhpFAz2weaSUKK0Pfybl\n" +
            "qAj+lug8aJRT7oM6iCsVlgmy4HqMLnXWnOunVmSPlk9orj2XwoSPwLxAwAtcvfaH\n" +
            "szVsrBhQf4TgTM2S0yDpM7xSma8ytSmzJSq0SPly4cpk9+aCEI3oncKKiPo4Zor8\n" +
            "Y/kB+Xj9e1x3+naH+uzfsQ55lVe0vSbv1gHR6xYKu44LtcXFilWr06zqkUspzBmk\n" +
            "MiVOKvFlRNACzqrOSbTqn3yDsEB750Orp2yjj32JgfpMpf/VjsPOS+C12LOORc92\n" +
            "wO1AK/1TD7Cn1TsNsYqiA94xrcx36m97PtbfkSIS5r762DL8EGMUUXLeXdYWk70p\n" +
            "aDPvOmbsB4om3xPXV2V4J95eSRQAogB/mqghtqmxlbCluQ0WEdrHbEg8QOB+DVrN\n" +
            "VjzRlwW5y0vtOUucxD/SVRNuJLDWcfr0wbrM7Rv1/oFB2ACYPTrIrnqYNxgFlQID\n" +
            "AQABo0IwQDAOBgNVHQ8BAf8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4E\n" +
            "FgQU5K8rJnEaK0gnhS9SZizv8IkTcT4wDQYJKoZIhvcNAQEMBQADggIBAJ+qQibb\n" +
            "C5u+/x6Wki4+omVKapi6Ist9wTrYggoGxval3sBOh2Z5ofmmWJyq+bXmYOfg6LEe\n" +
            "QkEzCzc9zolwFcq1JKjPa7XSQCGYzyI0zzvFIoTgxQ6KfF2I5DUkzps+GlQebtuy\n" +
            "h6f88/qBVRRiClmpIgUxPoLW7ttXNLwzldMXG+gnoot7TiYaelpkttGsN/H9oPM4\n" +
            "7HLwEXWdyzRSjeZ2axfG34arJ45JK3VmgRAhpuo+9K4l/3wV3s6MJT/KYnAK9y8J\n" +
            "ZgfIPxz88NtFMN9iiMG1D53Dn0reWVlHxYciNuaCp+0KueIHoI17eko8cdLiA6Ef\n" +
            "MgfdG+RCzgwARWGAtQsgWSl4vflVy2PFPEz0tv/bal8xa5meLMFrUKTX5hgUvYU/\n" +
            "Z6tGn6D/Qqc6f1zLXbBwHSs09dR2CQzreExZBfMzQsNhFRAbd03OIozUhfJFfbdT\n" +
            "6u9AWpQKXCBfTkBdYiJ23//OYb2MI3jSNwLgjt7RETeJ9r/tSQdirpLsQBqvFAnZ\n" +
            "0E6yove+7u7Y/9waLd64NnHi/Hm3lCXRSHNboTXns5lndcEZOitHTtNCjv0xyBZm\n" +
            "2tIMPNuzjsmhDYAPexZ3FL//2wmUspO8IFgV6dtxQ/PeEMMA3KgqlbbC1j+Qa3bb\n" +
            "bP6MvPJwNQzcmRk13NfIRmPVNnGuV/u3gm3c\n" +
            "-----END CERTIFICATE-----\n";
        const string GtsRootR4 =
            "-----BEGIN CERTIFICATE-----\n" +
            "MIICCTCCAY6gAwIBAgINAgPlwGjvYxqccpBQUjAKBggqhkjOPQQDAzBHMQswCQYD\n" +
            "VQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIG\n" +
            "A1UEAxMLR1RTIFJvb3QgUjQwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAwMDAw\n" +
            "WjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2Vz\n" +
            "IExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjQwdjAQBgcqhkjOPQIBBgUrgQQAIgNi\n" +
            "AATzdHOnaItgrkO4NcWBMHtLSZ37wWHO5t5GvWvVYRg1rkDdc/eJkTBa6zzuhXyi\n" +
            "QHY7qca4R9gq55KRanPpsXI5nymfopjTX15YhmUPoYRlBtHci8nHc8iMai/lxKvR\n" +
            "HYqjQjBAMA4GA1UdDwEB/wQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQW\n" +
            "BBSATNbrdP9JNqPV2Py1PsVq8JQdjDAKBggqhkjOPQQDAwNpADBmAjEA6ED/g94D\n" +
            "9J+uHXqnLrmvT/aDHQ4thQEd0dlq7A/Cr8deVl5c1RxYIigL9zC2L7F8AjEA8GE8\n" +
            "p/SgguMh1YQdc4acLa/KNJvxn7kjNuK8YAOdgLOaVsjh4rsUecrNIdSUtUlD\n" +
            "-----END CERTIFICATE-----\n";

        const int SyncEveryMinutes = 6 * 60;
        const int RetryAfterFailMinutes = 15;

        public delegate bool NetworkCheck();

        readonly Pcf85063Driver _rtc;
        readonly AutoResetEvent _syncWake = new AutoResetEvent(false);
        int _zone;
        bool _syncing;
        DateTime _lastSyncUtc;
        bool _everSynced;
        bool _lastFailed;

        public TimeService(Pcf85063Driver rtc)
        {
            _rtc = rtc;
            _zone = LoadZone();
            // Give TLS a plausible system clock (cert validity dates) before the first sync.
            DateTime utc;
            if (TryReadUtc(out utc))
            {
                try { nanoFramework.Runtime.Native.Rtc.SetSystemTime(utc); } catch { }
            }
        }

        public int Zone { get { return _zone; } }

        /// <summary>Selects and persists the time zone (index into the zone table).</summary>
        public void SetZone(int index)
        {
            if (index < 0 || index >= ZoneIds.Length) return;
            _zone = index;
            try { File.WriteAllText(ZoneFile, ZoneIds[index]); }
            catch (Exception ex) { Debug.WriteLine("[Time] zone save failed: " + ex.Message); }
        }

        /// <summary>Short status for Settings: "SYNCING", "NEVER", "FAILED", or the local time of the last
        /// successful sync ("15:32").</summary>
        public string SyncStatus
        {
            get
            {
                if (_syncing) return "SYNCING";
                if (!_everSynced) return _lastFailed ? "FAILED" : "NEVER";
                DateTime local = _lastSyncUtc.AddMinutes(OffsetMinutes(_lastSyncUtc));
                string hm = local.Hour.ToString("D2") + ":" + local.Minute.ToString("D2");
                return _lastFailed ? "FAILED " + hm : hm;
            }
        }

        /// <summary>Current local time (RTC UTC + the zone's offset incl. DST). False if the RTC is
        /// missing or its oscillator-stop flag says the time is invalid.</summary>
        public bool TryLocalNow(out Pcf85063Driver.RtcTime local)
        {
            local = new Pcf85063Driver.RtcTime();
            DateTime utc;
            if (!TryReadUtc(out utc)) return false;
            DateTime t = utc.AddMinutes(OffsetMinutes(utc));
            local.Year = t.Year; local.Month = t.Month; local.Day = t.Day;
            local.Hour = t.Hour; local.Minute = t.Minute; local.Second = t.Second;
            local.Weekday = (int)t.DayOfWeek;
            return true;
        }

        bool TryReadUtc(out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (_rtc == null) return false;
            try
            {
                Pcf85063Driver.RtcTime r;
                if (!_rtc.TryRead(out r)) return false;
                utc = new DateTime(r.Year, r.Month, r.Day, r.Hour, r.Minute, r.Second);
                return true;
            }
            catch { return false; } // garbage registers -> invalid date
        }

        /// <summary>Starts the background sync loop. <paramref name="networkUp"/> gates each attempt.</summary>
        public void StartAutoSync(NetworkCheck networkUp)
        {
            new Thread(() =>
            {
                _syncWake.WaitOne(15000, false); // let boot + WiFi settle (or sync now if asked)
                while (true)
                {
                    int waitMs = 30000; // no network yet: look again soon
                    if (networkUp == null || networkUp())
                        waitMs = (SyncOnce() ? SyncEveryMinutes : RetryAfterFailMinutes) * 60000;
                    _syncWake.WaitOne(waitMs, false);
                }
            }).Start();
        }

        /// <summary>Asks the sync loop to run now (Settings "SYNC" row).</summary>
        public void SyncNow() { _syncWake.Set(); }

        bool SyncOnce()
        {
            _syncing = true;
            try
            {
                DateTime utc;
                string source;
                if (!TryVerifiedTime(out utc, out source))
                {
                    // Maybe the system clock is too far off for certificate date checks: take an
                    // unverified time for the SYSTEM clock only, then verify again.
                    DateTime approx;
                    if (TryHttpsDate(SourceHosts[0], null, out approx))
                    {
                        Debug.WriteLine("[Time] verify failed; system clock bootstrapped from an unverified Date");
                        try { nanoFramework.Runtime.Native.Rtc.SetSystemTime(approx); } catch { }
                        TryVerifiedTime(out utc, out source);
                    }
                }
                if (utc == DateTime.MinValue)
                {
                    _lastFailed = true;
                    Debug.WriteLine("[Time] sync FAILED (no verified source)");
                    return false;
                }
                WriteRtc(utc);
                try { nanoFramework.Runtime.Native.Rtc.SetSystemTime(utc); } catch { }
                _lastSyncUtc = utc;
                _everSynced = true;
                _lastFailed = false;
                Debug.WriteLine("[Time] synced from " + source + ": " + utc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
                return true;
            }
            finally { _syncing = false; }
        }

        bool TryVerifiedTime(out DateTime utc, out string source)
        {
            for (int i = 0; i < SourceHosts.Length; i++)
            {
                if (TryHttpsDate(SourceHosts[i], i == 0 ? GtsRootR1 : GtsRootR4, out utc))
                {
                    source = SourceHosts[i];
                    return true;
                }
            }
            utc = DateTime.MinValue;
            source = null;
            return false;
        }

        // HEAD / over TLS and parse the Date header. caPem == null -> NO certificate verification.
        static bool TryHttpsDate(string host, string caPem, out DateTime utc)
        {
            utc = DateTime.MinValue;
            Socket sock = null;
            SslStream tls = null;
            try
            {
                IPAddress ip = Dns.GetHostEntry(host).AddressList[0];
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.ReceiveTimeout = 10000;
                sock.SendTimeout = 10000;
                sock.Connect(new IPEndPoint(ip, 443));
                tls = new SslStream(sock);
                if (caPem != null)
                {
                    tls.SslVerification = SslVerification.CertificateRequired;
                    tls.AuthenticateAsClient(host, null, new X509Certificate(caPem), SslProtocols.Tls12);
                }
                else
                {
                    tls.SslVerification = SslVerification.NoVerification;
                    tls.AuthenticateAsClient(host, SslProtocols.Tls12);
                }
                byte[] req = Encoding.UTF8.GetBytes("HEAD / HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");
                tls.Write(req, 0, req.Length);

                byte[] buf = new byte[2048];
                int n = 0;
                while (n < buf.Length)
                {
                    int r = tls.Read(buf, n, buf.Length - n);
                    if (r <= 0) break;
                    n += r;
                    if (IndexOfHeaderEnd(buf, n) >= 0) break;
                }
                string headers = new string(Encoding.UTF8.GetChars(buf, 0, n));
                return TryParseDateHeader(headers, out utc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Time] " + host + (caPem != null ? " (verified)" : " (unverified)") + ": " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
            finally
            {
                if (tls != null) { try { tls.Dispose(); } catch { } }
                if (sock != null) { try { sock.Close(); } catch { } }
            }
        }

        static int IndexOfHeaderEnd(byte[] b, int n)
        {
            for (int i = 3; i < n; i++)
                if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i;
            return -1;
        }

        // "Date: Thu, 01 Oct 2026 19:32:16 GMT" (RFC 7231 IMF-fixdate), header name case-insensitive.
        static bool TryParseDateHeader(string headers, out DateTime utc)
        {
            utc = DateTime.MinValue;
            int at = headers.ToLower().IndexOf("\ndate:");
            if (at < 0) return false;
            int eol = headers.IndexOf('\r', at + 1);
            if (eol < 0) eol = headers.Length;
            string v = headers.Substring(at + 6, eol - at - 6).Trim();
            string[] p = v.Split(' ');
            if (p.Length < 5) return false;
            int month = "JanFebMarAprMayJunJulAugSepOctNovDec".IndexOf(p[2]) / 3 + 1;
            string[] hms = p[4].Split(':');
            if (month < 1 || hms.Length != 3) return false;
            try
            {
                utc = new DateTime(int.Parse(p[3]), month, int.Parse(p[1]), int.Parse(hms[0]), int.Parse(hms[1]), int.Parse(hms[2]));
            }
            catch { return false; }
            return utc.Year >= 2025 && utc.Year < 2100;
        }

        void WriteRtc(DateTime utc)
        {
            if (_rtc == null) return;
            _rtc.Set(new Pcf85063Driver.RtcTime
            {
                Year = utc.Year, Month = utc.Month, Day = utc.Day,
                Hour = utc.Hour, Minute = utc.Minute, Second = utc.Second,
                Weekday = (int)utc.DayOfWeek,
            });
        }

        int LoadZone()
        {
            string id = DefaultZoneId;
            try { if (File.Exists(ZoneFile)) id = File.ReadAllText(ZoneFile).Trim(); }
            catch { }
            for (int i = 0; i < ZoneIds.Length; i++) if (ZoneIds[i] == id) return i;
            for (int i = 0; i < ZoneIds.Length; i++) if (ZoneIds[i] == DefaultZoneId) return i;
            return 0;
        }

        // ----- Offset with DST, evaluated at a UTC instant -----

        int OffsetMinutes(DateTime utc)
        {
            int std = ZoneStdMinutes[_zone];
            return InDst(ZoneDst[_zone], std, utc) ? std + 60 : std;
        }

        static bool InDst(byte rule, int stdMinutes, DateTime utc)
        {
            int y = utc.Year;
            switch (rule)
            {
                case DstUs:
                {
                    // 2nd Sunday of March 02:00 standard -> 1st Sunday of November 02:00 daylight (= 01:00 standard).
                    DateTime start = new DateTime(y, 3, NthSunday(y, 3, 2), 2, 0, 0).AddMinutes(-stdMinutes);
                    DateTime end = new DateTime(y, 11, NthSunday(y, 11, 1), 1, 0, 0).AddMinutes(-stdMinutes);
                    return utc >= start && utc < end;
                }
                case DstEu:
                {
                    // Last Sunday of March 01:00 UTC -> last Sunday of October 01:00 UTC.
                    DateTime start = new DateTime(y, 3, LastSunday(y, 3), 1, 0, 0);
                    DateTime end = new DateTime(y, 10, LastSunday(y, 10), 1, 0, 0);
                    return utc >= start && utc < end;
                }
                case DstAu:
                {
                    // Southern: off from 1st Sunday of April 03:00 daylight (= 02:00 standard) until
                    // 1st Sunday of October 02:00 standard.
                    DateTime end = new DateTime(y, 4, NthSunday(y, 4, 1), 2, 0, 0).AddMinutes(-stdMinutes);
                    DateTime start = new DateTime(y, 10, NthSunday(y, 10, 1), 2, 0, 0).AddMinutes(-stdMinutes);
                    return !(utc >= end && utc < start);
                }
                case DstNz:
                {
                    // Off from 1st Sunday of April 03:00 daylight until last Sunday of September 02:00 standard.
                    DateTime end = new DateTime(y, 4, NthSunday(y, 4, 1), 2, 0, 0).AddMinutes(-stdMinutes);
                    DateTime start = new DateTime(y, 9, LastSunday(y, 9), 2, 0, 0).AddMinutes(-stdMinutes);
                    return !(utc >= end && utc < start);
                }
                default:
                    return false;
            }
        }

        static int NthSunday(int year, int month, int n)
        {
            int dow = (int)new DateTime(year, month, 1).DayOfWeek; // 0 = Sunday
            return 1 + (7 - dow) % 7 + 7 * (n - 1);
        }

        static int LastSunday(int year, int month)
        {
            int days = DateTime.DaysInMonth(year, month);
            int dow = (int)new DateTime(year, month, days).DayOfWeek;
            return days - dow;
        }
    }
}
