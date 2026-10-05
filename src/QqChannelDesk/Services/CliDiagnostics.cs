using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public sealed class CliDiagnostics
{
    private readonly FfmpegManager _ffmpegManager;
    private const string MinimumCliVersion = "1.0.6";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NodeInstallTimeout = TimeSpan.FromMinutes(10);

    public CliDiagnostics(FfmpegManager? ffmpegManager = null)
    {
        _ffmpegManager = ffmpegManager ?? new FfmpegManager();
    }

    public async Task<DiagnosticReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        var node = await CheckIndependentlyAsync(
            "Node.js",
            () => CheckNodeAsync(cancellationToken),
            new DiagnosticItem(DiagnosticState.Error, "检查失败", "Node.js 检查失败"),
            log);

        string? cliPath = null;
        var cli = await CheckIndependentlyAsync(
            "CLI",
            async () =>
            {
                cliPath = await FindCliEntryPointAsync(cancellationToken);
                if (cliPath is null)
                    return new DiagnosticItem(DiagnosticState.Warning, "未安装", "未找到全局或当前目录的 tencent-channel-cli NPM 包");

                var versionResult = await RunNodeScriptAsync(cliPath, ["version"], cancellationToken);
                if (!versionResult.Started)
                    return new DiagnosticItem(DiagnosticState.Error, "无法启动", Sanitize(versionResult.ErrorMessage));

                var version = ExtractVersion(versionResult.CombinedOutput);
                var parsedVersion = ParseVersion(version);
                var versionOk = parsedVersion is not null && parsedVersion >= Version.Parse(MinimumCliVersion);
                return new DiagnosticItem(
                    versionOk ? DiagnosticState.Ready : DiagnosticState.Warning,
                    versionOk ? "可用" : "需升级或无法识别",
                    string.IsNullOrWhiteSpace(version) ? "版本命令没有返回可识别信息" : $"版本 {version}（最低要求 {MinimumCliVersion}）");
            },
            new DiagnosticItem(DiagnosticState.Error, "检查失败", "CLI 检查失败"),
            log);

        var ffmpeg = await CheckIndependentlyAsync(
            "FFmpeg",
            async () =>
            {
                var result = await _ffmpegManager.CheckAsync(cancellationToken);
                return new DiagnosticItem(
                    result.Available ? DiagnosticState.Ready : result.StateLabel == "异常" ? DiagnosticState.Error : DiagnosticState.Warning,
                    result.StateLabel,
                    result.Detail);
            },
            new DiagnosticItem(DiagnosticState.Error, "检查失败", "FFmpeg 检查失败"),
            log);

        var login = await CheckIndependentlyAsync(
            "登录状态",
            async () =>
            {
                if (cliPath is null || cli.State != DiagnosticState.Ready)
                    return new DiagnosticItem(DiagnosticState.Unknown, "未检查", "CLI 未通过检查，无法检查登录状态");

                var statusResult = await RunNodeScriptAsync(cliPath, ["login", "status"], cancellationToken);
                return ClassifyLoginStatus(statusResult.ExitCode, statusResult.CombinedOutput, statusResult.ErrorMessage);
            },
            new DiagnosticItem(DiagnosticState.Error, "检查失败", "登录状态检查失败"),
            log);

        var overallMessage = login.State == DiagnosticState.Ready
            ? "运行环境检查通过。"
            : cli.State != DiagnosticState.Ready
                ? "频道 CLI 未通过检查。"
                : "请按 CLI 官方扫码授权流程登录，然后重新检查";
        return new DiagnosticReport(node, cli, ffmpeg, login, log, overallMessage);
    }

    private static async Task<DiagnosticItem> CheckIndependentlyAsync(
        string name,
        Func<Task<DiagnosticItem>> check,
        DiagnosticItem failure,
        ICollection<string> log)
    {
        DiagnosticItem result;
        try
        {
            result = await check();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = failure with { Detail = Sanitize(ex.Message) };
        }

        log.Add($"{name}: {result.Detail}");
        return result;
    }

    public async Task<CliInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var node = await RunCommandAsync("node", ["--version"], cancellationToken);
        if (!node.Started || node.ExitCode != 0)
        {
            return new CliInstallResult(false, "未检测到可用的 Node.js。请先安装 Node.js，再重新检查。", null);
        }

        var result = await RunInstallCommandAsync(cancellationToken);
        if (!result.Started)
        {
            return new CliInstallResult(false, $"无法启动 npm 安装进程：{Sanitize(result.ErrorMessage)}", null);
        }

        if (result.ExitCode != 0)
        {
            var detail = result.ErrorMessage.Length > 0
                ? result.ErrorMessage
                : FirstUsefulLine(result.CombinedOutput, "npm 安装失败，请检查网络和全局目录权限");
            return new CliInstallResult(false, Sanitize(detail), result.CombinedOutput);
        }

        return new CliInstallResult(true, "npm 安装命令已成功完成。", result.CombinedOutput);
    }

    public async Task<CliInstallResult> InstallNodeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunCommandAsync("winget", [
            "install", "--id", "OpenJS.NodeJS.LTS", "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements"
        ], cancellationToken, timeoutDuration: NodeInstallTimeout);

        if (!result.Started)
        {
            return new CliInstallResult(false,
                "无法启动 winget。请从 nodejs.org 下载并安装 Node.js LTS，然后重新检查。", null);
        }

        RefreshNodePath();
        if (result.ExitCode != 0)
        {
            var detail = result.ErrorMessage.Length > 0
                ? result.ErrorMessage
                : FirstUsefulLine(result.CombinedOutput, "Node.js 自动安装失败，请使用手动安装入口");
            return new CliInstallResult(false, Sanitize(detail), result.CombinedOutput);
        }

        var check = await RunCommandAsync("node", ["--version"], cancellationToken);
        return check.Started && check.ExitCode == 0
            ? new CliInstallResult(true, $"Node.js {FirstUsefulLine(check.CombinedOutput)} 已安装并可用。", result.CombinedOutput)
            : new CliInstallResult(false, "winget 安装命令已完成，但当前进程尚未检测到 Node.js；请关闭并重新启动程序后再检查。", result.CombinedOutput);
    }

    public static void OpenNodeDownloadPage()
    {
        Process.Start(new ProcessStartInfo("https://nodejs.org/en/download") { UseShellExecute = true });
    }

    public static DiagnosticItem ClassifyLoginStatus(int? exitCode, string output, string error = "")
    {
        var rawText = $"{output}\n{error}";
        var message = ExtractCliMessage(rawText);
        var text = Sanitize(string.IsNullOrWhiteSpace(message) ? rawText : message);
        if (exitCode is null)
        {
            return new DiagnosticItem(DiagnosticState.Error, "检查失败", "CLI 未能返回有效状态");
        }

        if (ContainsAny(text, "未登录", "not logged in", "unauthorized", "未授权", "login required", "8011"))
        {
            return new DiagnosticItem(DiagnosticState.Warning, "未登录", "请使用 CLI 官方扫码授权流程登录");
        }

        if (ContainsAny(text, "expired", "过期", "token"))
        {
            return new DiagnosticItem(DiagnosticState.Warning, "授权可能过期", "请使用 CLI 官方流程重新授权");
        }

        var successFlag = TryReadSuccessFlag(rawText);
        if (successFlag == false || exitCode != 0)
            return new DiagnosticItem(DiagnosticState.Warning, "状态不确定", "CLI 未能确认当前登录状态，请重新检查或扫码登录");

        if (successFlag == true || ContainsAny(text, "logged in", "已登录", "登录状态正常", "authenticated", "authorized", "授权有效"))
            return new DiagnosticItem(DiagnosticState.Ready, "已登录", FirstUsefulLine(text, "CLI 登录状态正常"));

        return new DiagnosticItem(DiagnosticState.Warning, "状态不确定", "CLI 未能确认当前登录状态，请重新检查或扫码登录");
    }

    private static bool? TryReadSuccessFlag(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return success.GetBoolean();
        }
        catch (JsonException)
        {
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("success", out var success) &&
                    success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return success.GetBoolean();
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    public static string Sanitize(string value)
    {
        var safe = value ?? string.Empty;
        safe = Regex.Replace(safe, """(?i)(QQ_AI_CONNECT_TOKEN\s*[=:]\s*)[^\s"']+""", "$1[已隐藏]");
        safe = Regex.Replace(safe, """(?i)(access_token|refresh_token|authorization|cookie)\s*[=:]\s*[^\s,;"']+""", "$1=[已隐藏]");
        safe = Regex.Replace(safe, @"(?i)bot:v1_[A-Za-z0-9._-]+", "[凭证已隐藏]");
        return safe.Trim();
    }

    private static async Task<DiagnosticItem> CheckNodeAsync(CancellationToken cancellationToken)
    {
        RefreshNodePath();
        var result = await RunCommandAsync("node", ["--version"], cancellationToken);
        if (!result.Started)
        {
            return new DiagnosticItem(DiagnosticState.Error, "未安装", "PATH 中未找到 Node.js");
        }

        var version = FirstUsefulLine(result.CombinedOutput);
        return result.ExitCode == 0
            ? new DiagnosticItem(DiagnosticState.Ready, "可用", string.IsNullOrWhiteSpace(version) ? "Node.js 已启动" : version)
            : new DiagnosticItem(DiagnosticState.Error, "异常", FirstUsefulLine(result.CombinedOutput, "Node.js 检查失败"));
    }

    private static async Task<string?> FindCliEntryPointAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var npmCli = FindNpmCliPath();
        if (npmCli is not null)
        {
            var globalRoot = await RunCommandAsync("node", [npmCli, "root", "-g"], cancellationToken);
            if (globalRoot.ExitCode == 0)
            {
                var root = FirstUsefulLine(globalRoot.StandardOutput);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    candidates.Add(Path.Combine(root, "tencent-channel-cli", "bin", "tencent-channel-cli"));
                }
            }
        }

        candidates.Add(Path.Combine(Environment.CurrentDirectory, "node_modules", "tencent-channel-cli", "bin", "tencent-channel-cli"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "node_modules", "tencent-channel-cli", "bin", "tencent-channel-cli"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessResult> RunNodeScriptAsync(string scriptPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var args = new List<string> { scriptPath };
        args.AddRange(arguments);
        return await RunCommandAsync("node", args, cancellationToken, Path.GetDirectoryName(scriptPath));
    }

    private static async Task<ProcessResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        TimeSpan? timeoutDuration = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(false, null, "", "", "进程未能启动");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(false, null, "", "", Sanitize(ex.Message));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration ?? CommandTimeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(true, process.ExitCode, Sanitize(stdout), Sanitize(stderr), "");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            var timeoutMessage = timeoutDuration is null
                ? "命令超时"
                : fileName.Equals("winget", StringComparison.OrdinalIgnoreCase)
                    ? "Node.js 安装超过 10 分钟，已停止"
                    : "npm 安装超过 5 分钟，已停止";
            return new ProcessResult(true, null, "", "", timeoutMessage);
        }
    }

    private static async Task<ProcessResult> RunInstallCommandAsync(CancellationToken cancellationToken)
    {
        var npmCli = FindNpmCliPath();
        if (npmCli is null)
        {
            return new ProcessResult(false, null, "", "", "未找到 Node.js 附带的 npm-cli.js，请检查 Node.js/npm 安装");
        }

        return await RunCommandAsync(
            "node",
            [npmCli, "install", "-g", "tencent-channel-cli"],
            cancellationToken,
            timeoutDuration: InstallTimeout);
    }

    private static string? FindNpmCliPath()
    {
        RefreshNodePath();
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in pathEntries)
        {
            var nodePath = Path.Combine(entry.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (!File.Exists(nodePath))
            {
                continue;
            }

            var npmCliPath = Path.Combine(Path.GetDirectoryName(nodePath)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(npmCliPath))
            {
                return npmCliPath;
            }
        }

        return null;
    }

    private static void RefreshNodePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs")
        };
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missing = candidates.Where(path => File.Exists(Path.Combine(path, "node.exe")) &&
            !entries.Any(entry => string.Equals(entry.Trim('"'), path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, missing.Append(currentPath)), EnvironmentVariableTarget.Process);
    }

    private static Version? ParseVersion(string text)
    {
        var match = Regex.Match(text, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    private static string ExtractVersion(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String)
            {
                return version.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return FirstUsefulLine(output);
    }

    private static string ExtractCliMessage(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.String) return data.GetString() ?? string.Empty;
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var dataMessage) &&
                    dataMessage.ValueKind == JsonValueKind.String)
                {
                    return dataMessage.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static string FirstUsefulLine(string text, string fallback = "") =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? fallback;

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private sealed record ProcessResult(
        bool Started,
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        string ErrorMessage)
    {
        public string CombinedOutput => $"{StandardOutput}\n{StandardError}".Trim();
    }
}

public sealed record CliInstallResult(bool Succeeded, string Message, string? Output);
