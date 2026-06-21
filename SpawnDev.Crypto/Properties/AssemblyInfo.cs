using System.Reflection;

// SpawnDev.Crypto - native (Monocypher-backed) Ed25519 + X25519 for nanoFramework.
// Keep the version FIXED; the native firmware counterpart is checksum-matched to
// this assembly's metadata, and apps bind to it by identity. Bump deliberately
// only when the public API changes (which requires rebuilding the native stubs).
[assembly: AssemblyTitle("SpawnDev.Crypto")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Version of the NATIVE counterpart this managed assembly expects. The firmware's
// generated stub carries this + a checksum; the CLR refuses to deploy if they
// don't match. Bump when the native ABI changes.
[assembly: AssemblyNativeVersion("1.0.0.0")]
