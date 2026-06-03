using System;
using System.Diagnostics;
using System.Reflection;

namespace PKHeX.Core.AutoMod;

public static class ALMVersion
{
    private static readonly Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
    public static readonly AssemblyVersions Versions = new();

    public record AssemblyVersions
    {
        public readonly Version? AlmVersionCurrent = GetCurrentVersion("PKHeX.Core.AutoMod");

        public readonly Version? CoreVersionCurrent = GetCurrentVersion("PKHeX.Core");
        // Vendored: never hit the network for the "latest" version. We ship a fixed, matched
        // Core+ALM pair and GetIsMismatch() is hard-disabled, so this value is unused — leaving
        // the synchronous GitHub call in would only risk hangs/exceptions on the bot thread.
        public readonly Version? CoreVersionLatest = null;
    }

    /// <summary>
    /// Checks for plugin mismatch. If "EnableDevMode" is enabled it will allow a user to skip update warnings until the next release. Will otherwise check plugin mismatch for current versions.
    /// </summary>
    /// <returns>True if a plugin mismatch is found. False if any of the versions are null or no mismatch found.</returns>
    // Vendored: PKHeX.Core and this AutoMod build are shipped together as a deliberately matched
    // pair, so the assembly-version skew check (and its network call to GitHub) is not meaningful
    // here. Compatibility is managed at the distribution level instead.
    public static bool GetIsMismatch() => false;

    public static bool GetIsMismatchRaw() => GetIsMismatch(currentCore: Versions.CoreVersionCurrent, currentALM: Versions.AlmVersionCurrent, latestCore: Versions.CoreVersionLatest);

    public static bool GetIsMismatch(Version? currentCore, Version? currentALM, Version? latestCore)
    {
        if (currentCore is null || currentALM is null || latestCore is null)
            return false;

        var latestAllowed = new Version(APILegality.LatestAllowedVersion);
        return (APILegality.EnableDevMode && (latestCore > latestAllowed) && (latestCore > currentCore)) || (!APILegality.EnableDevMode && (currentCore > currentALM));
    }

    /// <summary>
    /// Gets the current version of the specified assembly.
    /// </summary>
    /// <returns>A version representing the current version of the specified assembly, or null if the assembly cannot be found or has no version available.</returns>
    private static Version? GetCurrentVersion(string assemblyName)
    {
        var assembly = Array.Find(assemblies, x => x.GetName().Name == assemblyName);
        return assembly?.GetName().Version;
    }

    private static Version? GetLatestCoreVersion()
    {
        try
        {
            return UpdateUtil.GetLatestPKHeXVersion();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }
}
