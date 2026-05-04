using System.Text.RegularExpressions;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Drift detector. The firmware (<c>SpawnWear/BleUuids.cs</c>) and the
/// Bridge (<c>SpawnWear.Bridge/BleUuids.cs</c>) hold MIRROR copies of
/// the BLE GATT UUIDs. They MUST stay byte-for-byte identical or the
/// PWA's characteristic lookups silently fail at runtime.
///
/// This test reads both files at test time, extracts every named GUID
/// constant via regex, and asserts that every name present in one file
/// resolves to the same UUID in the other. If you edit either file
/// without mirroring the change, this test fails immediately.
///
/// (When duplication graduates to a shared <c>SpawnWear.Protocol</c>
/// library, this test becomes obsolete and can be deleted.)
/// </summary>
public class BleUuidsParityTest
{
    static readonly string RepoRoot = LocateRepoRoot();
    static readonly string FirmwarePath = Path.Combine(RepoRoot, "SpawnWear", "BleUuids.cs");
    static readonly string BridgePath   = Path.Combine(RepoRoot, "SpawnWear.Bridge", "BleUuids.cs");

    [Fact]
    public void Firmware_and_bridge_BleUuids_match_byte_for_byte()
    {
        var fw = ParseGuidConstants(File.ReadAllText(FirmwarePath));
        var br = ParseGuidConstants(File.ReadAllText(BridgePath));

        Assert.NotEmpty(fw);
        Assert.NotEmpty(br);

        // Every UUID name in firmware must exist in bridge with the same value.
        foreach (var (name, guid) in fw)
        {
            Assert.True(br.TryGetValue(name, out var bridgeGuid),
                $"Firmware defines BLE UUID '{name}' but SpawnWear.Bridge/BleUuids.cs is missing it.");
            Assert.True(guid == bridgeGuid,
                $"BLE UUID '{name}' drifted: firmware={guid}, bridge={bridgeGuid}");
        }

        // Every UUID name in bridge must exist in firmware (catches Bridge
        // adding a UUID without firmware advertising it).
        foreach (var (name, guid) in br)
        {
            Assert.True(fw.TryGetValue(name, out var fwGuid),
                $"Bridge defines BLE UUID '{name}' but SpawnWear/BleUuids.cs is missing it.");
            Assert.True(guid == fwGuid,
                $"BLE UUID '{name}' drifted: bridge={guid}, firmware={fwGuid}");
        }
    }

    [Fact]
    public void Firmware_and_bridge_byte_constants_match()
    {
        // Same drift check for the byte constants (WifiCmd*, Button*,
        // Action*). Both sides need to agree on the on-the-wire byte values.
        var fw = ParseByteConstants(File.ReadAllText(FirmwarePath));
        var br = ParseByteConstants(File.ReadAllText(BridgePath));

        Assert.NotEmpty(fw);
        Assert.NotEmpty(br);

        foreach (var (name, value) in fw)
        {
            Assert.True(br.TryGetValue(name, out var bridgeValue),
                $"Firmware defines byte constant '{name}' but SpawnWear.Bridge/BleUuids.cs is missing it.");
            Assert.True(value == bridgeValue,
                $"Byte constant '{name}' drifted: firmware=0x{value:X2}, bridge=0x{bridgeValue:X2}");
        }
        foreach (var (name, value) in br)
        {
            Assert.True(fw.TryGetValue(name, out var fwValue),
                $"Bridge defines byte constant '{name}' but SpawnWear/BleUuids.cs is missing it.");
            Assert.True(value == fwValue,
                $"Byte constant '{name}' drifted: bridge=0x{value:X2}, firmware=0x{fwValue:X2}");
        }
    }

    static Dictionary<string, Guid> ParseGuidConstants(string source)
    {
        // Match e.g.   public static readonly Guid WifiServiceUuid = new("a0e4...fb");
        var rx = new Regex(@"Guid\s+(\w+Uuid)\s*=\s*new\s*\(\s*""([0-9a-fA-F\-]{36})""\s*\)",
                           RegexOptions.Compiled);
        var dict = new Dictionary<string, Guid>();
        foreach (Match m in rx.Matches(source))
        {
            if (Guid.TryParse(m.Groups[2].Value, out var g))
                dict[m.Groups[1].Value] = g;
        }
        return dict;
    }

    static Dictionary<string, byte> ParseByteConstants(string source)
    {
        // Match e.g.   public const byte WifiCmdConnect = 0x01;
        var rx = new Regex(@"const\s+byte\s+(\w+)\s*=\s*(0x[0-9a-fA-F]+|\d+)\s*;",
                           RegexOptions.Compiled);
        var dict = new Dictionary<string, byte>();
        foreach (Match m in rx.Matches(source))
        {
            string raw = m.Groups[2].Value;
            byte value;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToByte(raw.Substring(2), 16);
            else
                value = byte.Parse(raw);
            dict[m.Groups[1].Value] = value;
        }
        return dict;
    }

    static string LocateRepoRoot()
    {
        // Walk up from the test bin/ dir until we find SpawnWear.slnx.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "SpawnWear.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate SpawnWear repo root from " + AppContext.BaseDirectory);
    }
}
