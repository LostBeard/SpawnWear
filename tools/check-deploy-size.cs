// dotnet run tools/check-deploy-size.cs <bin/Debug>
//
// Reads every .pe in the supplied directory, sums them, and bails non-zero
// if total exceeds the nf-interpreter ESP32-S3 deploy ceiling we discovered
// 2026-05-04: ~289 KB at the wire-protocol level corresponds to ~234.5 KB
// of local .pe bytes (the wire format adds ~55 KB of assembly-table overhead).
//
// Configurations whose total local .pe sum exceeds 235 KB are at risk of
// silent flash corruption when deployed. nf-deploy reports 100% success but
// the on-flash assembly table is garbled starting at SpawnWear.pe.
//
// See feedback_nf_deploy_ceiling_298kb.md for the full investigation.
using System;
using System.IO;
using System.Linq;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: check-deploy-size <bin/Debug-dir> [--ceiling=235000]");
    return 2;
}

string dir = args[0];
int ceiling = 242500;
foreach (var a in args.Skip(1))
{
    if (a.StartsWith("--ceiling="))
    {
        ceiling = int.Parse(a.Substring("--ceiling=".Length));
    }
}

if (!Directory.Exists(dir))
{
    Console.Error.WriteLine($"directory not found: {dir}");
    return 2;
}

var peFiles = Directory.GetFiles(dir, "*.pe").OrderByDescending(p => new FileInfo(p).Length).ToArray();
if (peFiles.Length == 0)
{
    Console.Error.WriteLine($"no .pe files in {dir}");
    return 2;
}

long total = 0;
Console.WriteLine($"{"size",10}  {"file",-50}");
Console.WriteLine($"{new string('-', 10)}  {new string('-', 50)}");
foreach (var p in peFiles)
{
    var size = new FileInfo(p).Length;
    total += size;
    Console.WriteLine($"{size,10}  {Path.GetFileName(p),-50}");
}
Console.WriteLine($"{new string('-', 10)}  {new string('-', 50)}");
Console.WriteLine($"{total,10}  TOTAL");
Console.WriteLine();
Console.WriteLine($"ceiling: {ceiling}");
Console.WriteLine($"headroom: {ceiling - total} bytes");

if (total > ceiling)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"** DEPLOY-CEILING ALERT: total .pe = {total} > ceiling {ceiling} **");
    Console.Error.WriteLine($"** This deploy will likely CORRUPT the on-flash assembly table. **");
    Console.Error.WriteLine($"** See feedback_nf_deploy_ceiling_298kb.md before proceeding. **");
    Console.Error.WriteLine($"** To override (you've fixed nf-interpreter): pass --ceiling=NN. **");
    return 1;
}
return 0;
