// Read-only COM3 monitor to capture an ESP32 panic backtrace during a WebRTC connect crash.
// DTR/RTS disabled so opening the port does NOT reset the watch. Captures ~30s of raw output;
// the ESP-IDF panic ("Guru Meditation", "Backtrace:", register dump) is ASCII amid any binary.
#:package System.IO.Ports@9.0.0
using System;
using System.IO.Ports;
using System.Text;

var port = args.Length > 0 ? args[0] : "COM3";
var seconds = args.Length > 1 ? int.Parse(args[1]) : 30;

var sp = new SerialPort(port, 115200);
sp.DtrEnable = false;
sp.RtsEnable = false;
sp.ReadTimeout = 300;
try { sp.Open(); }
catch (Exception ex) { Console.WriteLine("OPEN FAIL " + port + ": " + ex.Message); return; }

Console.WriteLine($"[mon] {port} open (no DTR/RTS), capturing {seconds}s...");
var end = DateTime.UtcNow.AddSeconds(seconds);
var buf = new byte[4096];
var line = new StringBuilder();
while (DateTime.UtcNow < end)
{
    try
    {
        int n = sp.Read(buf, 0, buf.Length);
        for (int i = 0; i < n; i++)
        {
            byte b = buf[i];
            // print printable ASCII + newlines; show others as '.' so panic text stays readable
            if (b == (byte)'\n') { Console.WriteLine(line.ToString()); line.Clear(); }
            else if (b == (byte)'\r') { }
            else if (b >= 32 && b < 127) line.Append((char)b);
            else line.Append('.');
            if (line.Length > 500) { Console.WriteLine(line.ToString()); line.Clear(); }
        }
    }
    catch (TimeoutException) { }
}
if (line.Length > 0) Console.WriteLine(line.ToString());
sp.Close();
Console.WriteLine("[mon] done");
