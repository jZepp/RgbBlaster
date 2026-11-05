using System.Reflection;

namespace RgbBlaster.Core;

/// <summary>Version of the running build, from Directory.Build.props and the git commit it was built from.</summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Release number, for example "0.1.0".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>Short git commit, or null when the build had no git information.</summary>
    public static string? Commit => Informational.Split('+') is [_, var sha, ..] ? sha[..Math.Min(7, sha.Length)] : null;

    /// <summary>"0.1.0 (b2ee5d0)" or "0.1.0".</summary>
    public static string Display => Commit is { } c ? $"{Version} ({c})" : Version;
}
