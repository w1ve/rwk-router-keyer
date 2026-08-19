using System;
using System.Diagnostics;
using System.IO;

namespace RWK.Shared.Net;

/// <summary>
/// Resolves the path to the Tailscale sidecar executable relative to the running
/// application's base directory. This is the single auditable place for that decision.
/// <para>
/// The sidecar ships as a sibling in the release archive (16.6), so resolution is
/// always relative to the running executable — never the current working directory,
/// PATH, or any install registry.
/// </para>
/// </summary>
public static class SidecarPath
{
    /// <summary>
    /// The sidecar executable name on Windows.
    /// </summary>
    public const string DefaultExecutableName = "rwk-tailscale-sidecar.exe";

    /// <summary>
    /// Returns the base directory for a self-contained single-file app. This is the
    /// one place that knows which API to call. Uses <see cref="AppContext.BaseDirectory"/>
    /// which is the documented correct answer for single-file bundles.
    /// </summary>
    /// <returns>A non-empty, rooted path to the directory containing the running executable.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the base directory cannot be determined (empty or null), which would indicate
    /// a .NET runtime change that broke the assumption documented in requirement 16.8.
    /// </exception>
    public static string GetBaseDirectory()
    {
        string baseDirectory = AppContext.BaseDirectory;

        if (string.IsNullOrEmpty(baseDirectory))
        {
            // Fallback: try Environment.ProcessPath
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                baseDirectory = Path.GetDirectoryName(processPath) ?? string.Empty;
            }
        }

        if (string.IsNullOrEmpty(baseDirectory))
        {
            throw new InvalidOperationException(
                "Cannot determine the application base directory. " +
                "AppContext.BaseDirectory returned empty, and Environment.ProcessPath " +
                "is unavailable. This may indicate a .NET runtime change that broke " +
                "the single-file bundle base directory assumption (requirement 16.8).");
        }

        Debug.Assert(
            Path.IsPathRooted(baseDirectory),
            $"Base directory must be rooted but got: '{baseDirectory}'. " +
            "This indicates an unexpected .NET runtime behavior for single-file bundles.");

        return baseDirectory;
    }

    /// <summary>
    /// Pure function that resolves the sidecar executable path by combining the base
    /// directory with the executable name. Performs no file system probing — existence
    /// and executability are checked by the caller.
    /// </summary>
    /// <param name="baseDirectory">
    /// The directory containing the running executable, obtained from
    /// <see cref="GetBaseDirectory"/>. Must not be null or empty.
    /// </param>
    /// <param name="executableName">
    /// The bare file name of the sidecar executable (no directory separators).
    /// </param>
    /// <returns>
    /// <c>Path.Combine(baseDirectory, executableName)</c> — a sibling of the running
    /// executable and nothing else.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="baseDirectory"/> is null or empty. The guard exists
    /// specifically to catch an Assembly.Location value having leaked in, rather than
    /// letting it silently degrade to a working-directory-relative lookup.
    /// </exception>
    public static string Resolve(string baseDirectory, string executableName)
    {
        if (string.IsNullOrEmpty(baseDirectory))
        {
            throw new InvalidOperationException(
                "baseDirectory is null or empty. This typically means the caller used " +
                "Assembly.Location or Assembly.GetExecutingAssembly().Location, which " +
                "returns an empty string inside a self-contained single-file bundle. " +
                "Use AppContext.BaseDirectory or Path.GetDirectoryName(Environment.ProcessPath) " +
                "instead (requirement 16.8).");
        }

        return Path.Combine(baseDirectory, executableName);
    }
}
