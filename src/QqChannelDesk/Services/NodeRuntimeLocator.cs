using System.IO;

namespace QqChannelDesk.Services;

internal static class NodeRuntimeLocator
{
    private static readonly string NodeExecutableName = OperatingSystem.IsWindows() ? "node.exe" : "node";
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string? FindNodeExecutablePath(
        IEnumerable<string>? additionalDirectories = null,
        string? currentDirectory = null)
    {
        var normalizedCurrentDirectory = TryNormalizeDirectory(currentDirectory);
        var seen = new HashSet<string>(PathComparer);

        foreach (var directory in EnumerateTrustedNodeDirectories(additionalDirectories))
        {
            var normalizedDirectory = TryNormalizeDirectory(directory);
            if (normalizedDirectory is null || !seen.Add(normalizedDirectory)) continue;
            if (normalizedCurrentDirectory is not null &&
                PathComparer.Equals(normalizedDirectory, normalizedCurrentDirectory)) continue;

            var candidate = Path.Combine(normalizedDirectory, NodeExecutableName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static string? FindNpmCliPath(string nodeExecutablePath)
    {
        var nodeDirectory = Path.GetDirectoryName(nodeExecutablePath);
        if (string.IsNullOrWhiteSpace(nodeDirectory)) return null;

        var npmCliPath = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        return File.Exists(npmCliPath) ? Path.GetFullPath(npmCliPath) : null;
    }

    public static string? ResolveConfiguredExecutable(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || IsDefaultNodeCommand(configuredPath))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (!Path.IsPathRooted(expanded)) return null;

        try
        {
            var fullPath = Path.GetFullPath(expanded);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsDefaultNodeCommand(string path)
    {
        var value = path.Trim().Trim('"');
        return string.Equals(value, "node", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "node.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string?> EnumerateTrustedNodeDirectories(
        IEnumerable<string>? additionalDirectories)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "node");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "nodejs");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "nodejs");

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in pathEntries) yield return entry;

        if (additionalDirectories is null) yield break;
        foreach (var directory in additionalDirectories) yield return directory;
    }

    private static string? TryNormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathRooted(expanded)) return null;

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
