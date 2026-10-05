using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security;
using System.Text;

namespace QqChannelDesk.Services;

public static class MachineCodeProvider
{
    private const string CodePrefix = "QCD-";

    public static string GetMachineCode()
    {
        var machineGuid = ReadMachineGuid();
        var source = string.IsNullOrWhiteSpace(machineGuid)
            ? $"fallback|{Environment.MachineName}|{Environment.OSVersion.VersionString}"
            : $"machine-guid|{machineGuid}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"QqChannelDesk|{source}"));
        return CodePrefix + Convert.ToHexString(hash[..8]);
    }

    private static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key?.GetValue("MachineGuid") is string value && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            catch (SecurityException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
