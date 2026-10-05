using System.Reflection;

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
}
