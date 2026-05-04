using System.Linq;
using System.Reflection;

namespace SpawnWear.Companion;

/// <summary>Compile-time build metadata for the running Companion. The
/// <see cref="Timestamp"/> value is set by an <c>AssemblyMetadataAttribute</c>
/// in <c>SpawnWear.Companion.csproj</c> with key "BuildTimestamp" - changes
/// every build so the live app can prove which bytes are loaded.</summary>
public static class BuildInfo
{
    public static readonly string Timestamp =
        typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value
        ?? "unknown";
}
