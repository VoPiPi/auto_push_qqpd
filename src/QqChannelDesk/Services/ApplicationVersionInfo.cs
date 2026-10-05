using System.Reflection;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public static class ApplicationVersionInfo
{
    public static string CurrentVersion => FormatVersion(Assembly.GetEntryAssembly());

    public static string FormatVersion(Assembly? assembly)
    {
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        var version = assembly?.GetName().Version;
        return version is null ? "未知" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        var match = Regex.Match(value.Trim(), @"(?<!\d)(\d+(?:\.\d+){1,3})(?![\d.])", RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        var numericVersion = match.Groups[1].Value;
        var components = numericVersion.Count(character => character == '.') + 1;
        if (components == 2) numericVersion += ".0";
        if (!Version.TryParse(numericVersion, out var parsed) || parsed is null) return false;
        version = parsed;
        return true;
    }
}
