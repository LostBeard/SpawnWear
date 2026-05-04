// dotnet run tools/check-pe-header.cs <path-to.pe>
//
// Pre-flight inspection for a managed nanoFramework .pe assembly. Decodes
// the CLR_RECORD_ASSEMBLY header from the first ~64 bytes and reports:
//   - 8-byte marker (must be the standard nanoFramework signature)
//   - headerCRC + assemblyCRC
//   - flags (bit 0 = c_Flags_NeedReboot - if set, Assembly.Load(byte[])
//           returns CLR_E_BUSY and the assembly needs a reboot to load)
//   - nativeMethodsChecksum
//   - patchEntryOffset
//   - version (major.minor.build.revision)
//   - assemblyName string-table index
//   - stringTableVersion
//
// Layout reference: nf-interpreter/src/CLR/Include/nanoCLR_Types.h:981
//   struct CLR_RECORD_ASSEMBLY {
//       static const CLR_UINT32 c_Flags_NeedReboot = 0x00000001;
//       CLR_UINT8 marker[8];                    // offset 0
//       CLR_UINT32 headerCRC;                   // offset 8
//       CLR_UINT32 assemblyCRC;                 // offset 12
//       CLR_UINT32 flags;                       // offset 16   <- the one we care about
//       CLR_UINT32 nativeMethodsChecksum;       // offset 20
//       CLR_UINT32 patchEntryOffset;            // offset 24
//       CLR_RECORD_VERSION version;             // offset 28 (4 x UINT16 = 8 bytes)
//       CLR_STRING assemblyName;                // offset 36 (UINT16 string-table index)
//       CLR_UINT16 stringTableVersion;          // offset 38
//       ...
//   };
//
// Note: Gemini's reply 2026-05-04 said the flags field is at offset 12.
// That's WRONG - offset 12 is assemblyCRC. Verified against the source.
using System;
using System.IO;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: check-pe-header <path-to.pe>");
    return 2;
}

byte[] header = new byte[64];
using (var fs = File.OpenRead(args[0]))
{
    int read = fs.Read(header, 0, header.Length);
    if (read < 40)
    {
        Console.Error.WriteLine($"file too short: only {read} bytes");
        return 2;
    }
}

uint headerCRC          = BitConverter.ToUInt32(header,  8);
uint assemblyCRC        = BitConverter.ToUInt32(header, 12);
uint flags              = BitConverter.ToUInt32(header, 16);
uint nativeChecksum     = BitConverter.ToUInt32(header, 20);
uint patchEntryOffset   = BitConverter.ToUInt32(header, 24);
ushort verMajor         = BitConverter.ToUInt16(header, 28);
ushort verMinor         = BitConverter.ToUInt16(header, 30);
ushort verBuild         = BitConverter.ToUInt16(header, 32);
ushort verRevision      = BitConverter.ToUInt16(header, 34);
ushort assemblyNameIdx  = BitConverter.ToUInt16(header, 36);
ushort stringTableVer   = BitConverter.ToUInt16(header, 38);

Console.WriteLine($"file:                  {args[0]}");
Console.Write   ($"marker [0..8]:         ");
for (int i = 0; i < 8; i++) Console.Write($"{header[i]:X2} ");
Console.WriteLine();
Console.WriteLine($"headerCRC:             0x{headerCRC:X8}");
Console.WriteLine($"assemblyCRC:           0x{assemblyCRC:X8}");
Console.WriteLine($"flags:                 0x{flags:X8}{(((flags & 1u) != 0) ? "  c_Flags_NeedReboot SET (Assembly.Load(byte[]) will return CLR_E_BUSY)" : "  no flags set")}");
Console.WriteLine($"nativeMethodsChecksum: 0x{nativeChecksum:X8}");
Console.WriteLine($"patchEntryOffset:      0x{patchEntryOffset:X8}");
Console.WriteLine($"version:               {verMajor}.{verMinor}.{verBuild}.{verRevision}");
Console.WriteLine($"assemblyName index:    {assemblyNameIdx} (offset into string table)");
Console.WriteLine($"stringTableVersion:    {stringTableVer}");

// Exit 0 if the assembly is loadable at runtime, 1 if it requires a reboot.
return (flags & 1u) != 0 ? 1 : 0;
