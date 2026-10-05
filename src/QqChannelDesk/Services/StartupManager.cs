using Microsoft.Win32;
using System.IO;

namespace QqChannelDesk.Services;

/// <summary>
/// Manages the current user's Windows logon entry. This intentionally does not
/// create a service or add any command-line switch that could publish silently.
/// </summary>
public sealed class StartupManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApplicationValueName = "QqChannelDesk";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ApplicationValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled, string? executablePath = null)
    {
        if (enabled)
        {
            var path = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("无法确定当前程序路径，未能设置开机启动。" );

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。" );
            key.SetValue(ApplicationValueName, BuildRunCommand(path), RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        existing?.DeleteValue(ApplicationValueName, throwOnMissingValue: false);
    }

    public static string BuildRunCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("程序路径不能为空。", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath.Trim());
        return $"\"{fullPath.Replace("\"", "\\\"")}\"";
    }
}
