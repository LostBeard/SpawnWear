// Repack nanoFramework.Graphics + nanoFramework.Graphics.Core +
// nanoFramework.Graphics.Co5300 into -spawnwear.<N> local nupkgs sourced from
// the LostBeard fork build outputs.
//
// Usage:
//     dotnet run tools/nf-graphics-repack.cs <new-suffix>
//
// Example: dotnet run tools/nf-graphics-repack.cs spawnwear.2
//
// Output: three .nupkg files staged in %TEMP%\nf-graphics-repack, then registered in the local feed
// D:\users\SpawnDevPackages with `nuget add`, ready for the SpawnWear .nfproj (HintPath references)
// after a packages.config restore.

#:package System.IO.Compression@4.3.0

using System.IO.Compression;
using System.Xml.Linq;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run tools/nf-graphics-repack.cs <new-suffix>");
    Console.Error.WriteLine("       new-suffix is the prerelease tag part, e.g. spawnwear.2");
    return 1;
}

string suffix = args[0];
string newVersion = $"2.0.0-{suffix}";
const string baseVersion = "2.0.0-spawnwear.1";
const string graphicsRoot = @"D:\users\tj\Projects\nanoFramework.Graphics\nanoFramework.Graphics";
// The feed is HIERARCHICAL (id/version/): packages go in with `nuget add`, never as a copy into its root.
const string feedDir = @"D:\users\SpawnDevPackages";
// nuget.exe is not on the agent PATH (and the .cargo nuget.exe that is, is not NuGet) - use the WinGet install
const string nugetExe = @"C:\Users\TJ\AppData\Local\Microsoft\WinGet\Packages\Microsoft.NuGet_Microsoft.Winget.Source_8wekyb3d8bbwe\nuget.exe";
string outputDir = Path.Combine(Path.GetTempPath(), "nf-graphics-repack");

var packages = new (string Name, string BinDir, string AssemblyName, string Description)[]
{
    ("nanoFramework.Graphics.Core",
     Path.Combine(graphicsRoot, "nanoFramework.Graphics.Core", "bin", "Release"),
     "nanoFramework.Graphics.Core",
     "SpawnWear-pinned build of nanoFramework.Graphics.Core (Color + DisplayBusType + GraphicDriver + DisplayOrientation + SetWindowType)."),
    ("nanoFramework.Graphics",
     Path.Combine(graphicsRoot, "nanoFramework.Graphics", "bin", "Release"),
     "nanoFramework.Graphics",
     "SpawnWear-pinned build of nanoFramework.Graphics with Sleep / Wake / SetBrightness API and Co5300 hybrid-QSPI panel support. Local feed package; not for nuget.org."),
    ("nanoFramework.Graphics.Co5300",
     Path.Combine(graphicsRoot, "ManagedDrivers", "Co5300", "bin", "Release"),
     "nanoFramework.Graphics.Co5300",
     "SpawnWear-pinned build of the CO5300 managed display driver descriptor for the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch."),
};

Directory.CreateDirectory(outputDir);

foreach (var pkg in packages)
{
    string outputPath = Path.Combine(outputDir, $"{pkg.Name}.{newVersion}.nupkg");
    Console.WriteLine($"Repacking {pkg.Name} {newVersion} -> {outputPath}");

    if (File.Exists(outputPath)) File.Delete(outputPath);

    foreach (string ext in new[] { ".dll", ".pe", ".pdbx" })
    {
        string sourceFile = Path.Combine(pkg.BinDir, pkg.AssemblyName + ext);
        if (!File.Exists(sourceFile))
        {
            Console.Error.WriteLine($"  MISSING: {sourceFile}");
            return 2;
        }
    }

    using var fs = File.Create(outputPath);
    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

    foreach (string ext in new[] { ".dll", ".pe", ".pdbx" })
    {
        string sourceFile = Path.Combine(pkg.BinDir, pkg.AssemblyName + ext);
        string entryPath = $"lib/{pkg.AssemblyName}{ext}";
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var src = File.OpenRead(sourceFile);
        src.CopyTo(es);
    }

    string nuspecXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2011/10/nuspec.xsd"">
  <metadata>
    <id>{pkg.Name}</id>
    <version>{newVersion}</version>
    <authors>nanoframework, LostBeard</authors>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <description>{pkg.Description}</description>
  </metadata>
</package>
";
    var nuspecEntry = zip.CreateEntry($"{pkg.Name}.nuspec", CompressionLevel.Optimal);
    using (var nse = nuspecEntry.Open())
    using (var sw = new StreamWriter(nse))
    {
        sw.Write(nuspecXml);
    }

    var contentTypesEntry = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
    using (var cte = contentTypesEntry.Open())
    using (var sw = new StreamWriter(cte))
    {
        sw.Write(
@"<?xml version=""1.0"" encoding=""utf-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml"" />
  <Default Extension=""nuspec"" ContentType=""application/octet"" />
  <Default Extension=""dll"" ContentType=""application/octet"" />
  <Default Extension=""pe"" ContentType=""application/octet"" />
  <Default Extension=""pdbx"" ContentType=""application/octet"" />
</Types>
");
    }

    var relsEntry = zip.CreateEntry("_rels/.rels", CompressionLevel.Optimal);
    using (var re = relsEntry.Open())
    using (var sw = new StreamWriter(re))
    {
        sw.Write($@"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Type=""http://schemas.microsoft.com/packaging/2010/07/manifest"" Target=""/{pkg.Name}.nuspec"" Id=""R0"" />
</Relationships>
");
    }
}

foreach (var pkg in packages)
{
    string nupkg = Path.Combine(outputDir, $"{pkg.Name}.{newVersion}.nupkg");
    // an earlier version of this tool wrote flat .nupkg files into the feed root - remove that copy if present
    string flat = Path.Combine(feedDir, Path.GetFileName(nupkg));
    if (File.Exists(flat)) { File.Delete(flat); Console.WriteLine($"Removed flat feed-root copy {flat}"); }
    var add = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(nugetExe, $"add \"{nupkg}\" -source \"{feedDir}\"") { UseShellExecute = false })!;
    add.WaitForExit();
    if (add.ExitCode != 0) { Console.Error.WriteLine($"nuget add failed ({add.ExitCode}) for {nupkg}"); return 3; }
}

Console.WriteLine();
Console.WriteLine($"Repacked 3 packages as {newVersion} and added them to {feedDir}.");
Console.WriteLine();
Console.WriteLine("Next: update SpawnWear/SpawnWear.nfproj <Reference HintPath> to point at");
Console.WriteLine($"      ..\\packages\\<package>.{newVersion}\\lib\\<assembly>.dll");
Console.WriteLine("      then dotnet restore (or just build - msbuild will pick the new version up).");
return 0;
