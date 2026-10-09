using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public sealed class CliDiagnostics
{
    private readonly FfmpegManager _ffmpegManager;
    private readonly string? _cliEntryPoint;
    private const string MinimumCliVersion = "1.0.6";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NodeInstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan VersionIndexTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadHeadersTimeout = TimeSpan.FromSeconds(30);

    public CliDiagnostics(FfmpegManager? ffmpegManager = null, string? cliEntryPoint = null)
    {
        _ffmpegManager = ffmpegManager ?? new FfmpegManager();
        _cliEntryPoint = cliEntryPoint;
    }

    public async Task<DiagnosticReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        var nodeLog = new List<string>();
        var cliLog = new List<string>();
        var ffmpegLog = new List<string>();

        string? cliPath = null;
        var nodeTask = CheckIndependentlyAsync(
            "Node.js",
            () => CheckNodeAsync(cancellationToken),
            new DiagnosticItem(DiagnosticState.Error, "检查失败", "Node.js 检查失败"),
            nodeLog);

        var cliTask = CheckIndependentlyAsync(
            "CLI",
            async () =>
            {
                cliPath = _cliEntryPoint ?? await FindCliEntryPointAsync(cancellationToken);
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
            cliLog);

        var ffmpegTask = CheckIndependentlyAsync(
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
            ffmpegLog);

        await Task.WhenAll(nodeTask, cliTask, ffmpegTask);
        var node = await nodeTask;
        var cli = await cliTask;
        var ffmpeg = await ffmpegTask;
        log.AddRange(nodeLog);
        log.AddRange(cliLog);
        log.AddRange(ffmpegLog);

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

    public Task<CliInstallResult> InstallNodeAsync(CancellationToken cancellationToken = default) =>
        InstallNodeAsync(progress: null, cancellationToken);

    public async Task<CliInstallResult> InstallNodeAsync(
        IProgress<NodeInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ReportNodeProgress(progress, 8, "正在准备 Node.js 官方安装包…",
            "将从 nodejs.org 获取 LTS 安装包，不依赖 winget。");
        return await InstallNodeFromOfficialMsiAsync(progress, cancellationToken);
    }

    private async Task<CliInstallResult> InstallNodeFromOfficialMsiAsync(
        IProgress<NodeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? msiPath = null;
        try
        {
            using var systemHttp = CreateNodeHttpClient(useProxy: true);
            using var directHttp = CreateNodeHttpClient(useProxy: false);

            // 官方源优先；国内网络下索引可能长时间不结束，超时后自动使用 npmmirror 镜像。
            var sources = new[]
            {
                new NodeDownloadSource("Node.js 官方源", "https://nodejs.org/dist/index.json", "https://nodejs.org/dist/"),
                new NodeDownloadSource("npmmirror 国内镜像", "https://npmmirror.com/mirrors/node/index.json", "https://npmmirror.com/mirrors/node/")
            };
            var routes = sources.SelectMany(source => new[]
            {
                new NodeDownloadRoute(source, systemHttp, "Windows 系统网络设置"),
                new NodeDownloadRoute(source, directHttp, "直接连接")
            }).ToArray();
            string? ltsVersion = null;
            NodeDownloadRoute? selectedRoute = null;
            string? lastVersionError = null;
            for (var routeIndex = 0; routeIndex < routes.Length; routeIndex++)
            {
                var route = routes[routeIndex];
                var source = route.Source;
                ReportNodeProgress(progress, routeIndex == 0 ? 22 : 24, "正在读取 Node.js LTS 版本…",
                    $"连接方式：{route.ConnectionName}\n请求地址：{source.IndexUrl}\n若该连接无响应，将在 {VersionIndexTimeout.TotalSeconds:0} 秒后自动尝试下一种方式。");
                try
                {
                    var indexJson = await GetStringWithTimeoutAsync(
                        route.Client, source.IndexUrl, VersionIndexTimeout, cancellationToken);
                    using var document = JsonDocument.Parse(indexJson);
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        if (entry.TryGetProperty("lts", out var lts) && lts.ValueKind != JsonValueKind.False &&
                            entry.TryGetProperty("version", out var version))
                        {
                            ltsVersion = version.GetString();
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(ltsVersion))
                    {
                        selectedRoute = route;
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastVersionError = ex.Message;
                    ReportNodeProgress(progress, routeIndex == 0 ? 23 : 24,
                        $"{source.Name}请求失败，正在尝试下一种连接方式…",
                        $"连接方式：{route.ConnectionName}\n请求地址：{source.IndexUrl}\n失败原因：{Sanitize(ex.Message)}");
                }
            }

            if (string.IsNullOrWhiteSpace(ltsVersion))
            {
                return new CliInstallResult(false,
                    string.IsNullOrWhiteSpace(lastVersionError)
                        ? "未能获取 Node.js LTS 版本信息。请检查网络后重试，或从 nodejs.org 手动下载安装。"
                        : $"未能获取 Node.js LTS 版本信息（{Sanitize(lastVersionError)}）。请检查网络后重试，或从 nodejs.org 手动下载安装。", null);
            }

            msiPath = Path.Combine(Path.GetTempPath(), $"node-{ltsVersion}-x64.msi");
            Exception? lastDownloadError = null;
            var downloadRoutes = new[] { selectedRoute! }
                .Concat(routes.Where(route => route != selectedRoute))
                .ToArray();
            for (var routeIndex = 0; routeIndex < downloadRoutes.Length; routeIndex++)
            {
                var route = downloadRoutes[routeIndex];
                var source = route.Source;
                var downloadUrl = $"{source.DistBaseUrl.TrimEnd('/')}/{ltsVersion}/node-{ltsVersion}-x64.msi";
                ReportNodeProgress(progress, routeIndex == 0 ? 25 : 26, $"已找到 Node.js {ltsVersion}，正在连接下载源…",
                    $"来源：{source.Name} · {route.ConnectionName}\n请求地址：{downloadUrl}");
                try
                {
                    using var response = await GetResponseWithTimeoutAsync(
                        route.Client, downloadUrl, DownloadHeadersTimeout, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;
                    await using (var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .WaitAsync(cancellationToken))
                    await using (var target = new FileStream(msiPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long downloadedBytes = 0;
                        var lastReportedPercent = -1;
                        int read;
                        while (true)
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            readCts.CancelAfter(DownloadHeadersTimeout);
                            try
                            {
                                read = await stream
                                    .ReadAsync(buffer, readCts.Token)
                                    .AsTask()
                                    .WaitAsync(readCts.Token);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new TimeoutException(
                                    $"下载连接连续 {DownloadHeadersTimeout.TotalSeconds:0} 秒没有收到数据。");
                            }

                            if (read == 0)
                                break;

                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                                .AsTask()
                                .WaitAsync(cancellationToken);
                            downloadedBytes += read;
                            var percent = totalBytes is > 0
                                ? 26 + Math.Min(58, 58d * downloadedBytes / totalBytes.Value)
                                : 55;
                            var roundedPercent = (int)Math.Floor(percent);
                            if (roundedPercent != lastReportedPercent)
                            {
                                lastReportedPercent = roundedPercent;
                                ReportNodeProgress(progress, percent,
                                    $"正在下载 Node.js 安装包… {FormatBytes(downloadedBytes)} / {(totalBytes is > 0 ? FormatBytes(totalBytes.Value) : "未知大小")}",
                                    $"Node.js {ltsVersion} · 来源：{source.Name} · {route.ConnectionName}\n请求地址：{downloadUrl}");
                            }
                        }
                    }

                    lastDownloadError = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastDownloadError = ex;
                    ReportNodeProgress(progress, routeIndex == 0 ? 25 : 26,
                        $"{source.Name}下载失败，正在尝试下一种连接方式…",
                        $"连接方式：{route.ConnectionName}\n请求地址：{downloadUrl}\n失败原因：{Sanitize(ex.Message)}");
                }
            }

            if (lastDownloadError is not null)
            {
                return new CliInstallResult(false,
                    $"Node.js 安装包下载失败：{Sanitize(lastDownloadError.Message)}。请检查网络后重试，或从 nodejs.org 手动下载安装。", null);
            }

            ReportNodeProgress(progress, 86, "下载完成，正在启动 Windows 安装器…",
                "如果出现“用户账户控制”或安装向导窗口，请按提示完成授权或安装。",
                canCancel: false);
            cancellationToken.ThrowIfCancellationRequested();
            // /passive 只显示进度条；按机器安装时 Windows 会自动弹出 UAC 授权提示。
            // 该启动过程在等待 UAC 授权时会阻塞调用线程，必须放到后台线程执行，
            // 否则界面在此处会停止响应（表现为程序"未响应"）。
            using var installer = await Task.Run(() => Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /passive")
            {
                UseShellExecute = true
            }), cancellationToken);
            if (installer is null)
            {
                return new CliInstallResult(false, "无法启动 Node.js 安装程序（msiexec）。请从 nodejs.org 手动下载安装。", null);
            }

            ReportNodeProgress(progress, 90, "Windows 安装器正在安装 Node.js…",
                "安装期间请不要关闭进度窗口。", canCancel: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NodeInstallTimeout);
            try
            {
                await installer.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CliInstallResult(false, "等待 Node.js 安装程序超时。若安装仍在进行，请完成安装后重新检查。", null);
            }

            RefreshNodePath();
            ReportNodeProgress(progress, 95, "Windows 安装器已结束，正在验证 Node.js…",
                "正在检查 node --version。", canCancel: false);
            var check = await RunCommandAsync("node", ["--version"], cancellationToken);
            ReportNodeProgress(progress, 100, check.Started && check.ExitCode == 0
                ? "Node.js 安装完成"
                : "Windows 安装器已结束，但 Node.js 验证未通过",
                canCancel: false);
            if (check.Started && check.ExitCode == 0)
            {
                return new CliInstallResult(true, $"Node.js {FirstUsefulLine(check.CombinedOutput)} 已通过官方安装包安装并可用。", null);
            }

            return installer.ExitCode == 0
                ? new CliInstallResult(false, "Node.js 安装程序已完成，但当前进程尚未检测到 Node.js；请关闭并重新启动程序后再检查。", null)
                : new CliInstallResult(false, $"Node.js 安装程序已退出（代码 {installer.ExitCode}）。可能取消了授权或安装被拒绝，可重试或从 nodejs.org 手动安装。", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CliInstallResult(false, $"Node.js 官方安装包下载或启动失败：{Sanitize(ex.Message)}。请从 nodejs.org 手动下载安装。", null);
        }
        finally
        {
            try
            {
                if (msiPath is not null && File.Exists(msiPath))
                    File.Delete(msiPath);
            }
            catch
            {
                // 临时安装包清理失败不影响安装结果。
            }
        }
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

    private async Task<DiagnosticItem> CheckNodeAsync(CancellationToken cancellationToken)
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

    private async Task<string?> FindCliEntryPointAsync(CancellationToken cancellationToken)
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

    private async Task<ProcessResult> RunNodeScriptAsync(string scriptPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var args = new List<string> { scriptPath };
        args.AddRange(arguments);
        var path = await _ffmpegManager.BuildCliPathAsync(
            Environment.GetEnvironmentVariable("PATH"), cancellationToken).ConfigureAwait(false);
        return await RunCommandAsync(
            "node", args, cancellationToken, Path.GetDirectoryName(scriptPath), pathOverride: path);
    }

    private async Task<ProcessResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        TimeSpan? timeoutDuration = null,
        string? pathOverride = null)
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
        if (pathOverride is not null)
            process.StartInfo.Environment["PATH"] = pathOverride;
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
            if (cancellationToken.IsCancellationRequested) throw;
            var timeoutMessage = timeoutDuration is null
                ? "命令超时"
                : fileName.Equals("winget", StringComparison.OrdinalIgnoreCase)
                    ? "Node.js 安装超过 10 分钟，已停止"
                    : "npm 安装超过 5 分钟，已停止";
            return new ProcessResult(true, null, "", "", timeoutMessage);
        }
    }

    private async Task<ProcessResult> RunInstallCommandAsync(CancellationToken cancellationToken)
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

    private static void ReportNodeProgress(
        IProgress<NodeInstallProgress>? progress,
        double percent,
        string message,
        string? detail = null,
        bool canCancel = true)
    {
        progress?.Report(new NodeInstallProgress(Math.Clamp(percent, 0, 100), message, detail, canCancel));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    internal static async Task<string> GetStringWithTimeoutAsync(
        HttpClient http,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await RunHttpOperationWithHardTimeoutAsync(
            token => http.GetStringAsync(url, token), url, timeout, cancellationToken);
    }

    private static Task<HttpResponseMessage> GetResponseWithTimeoutAsync(
        HttpClient http,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunHttpOperationWithHardTimeoutAsync(
            token => http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token),
            url,
            timeout,
            cancellationToken);

    private static async Task<T> RunHttpOperationWithHardTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestTask = Task.Run(() => operation(requestCts.Token), CancellationToken.None);
        try
        {
            return await requestTask.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            requestCts.Cancel();
            ObserveFault(requestTask);
            throw new TimeoutException($"请求 {url} 超过 {timeout.TotalSeconds:0} 秒未响应。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            requestCts.Cancel();
            ObserveFault(requestTask);
            throw;
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static HttpClient CreateNodeHttpClient(bool useProxy)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = useProxy,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        if (useProxy)
            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;

        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QqChannelDesk/1.0");
        return http;
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

    private sealed record NodeDownloadSource(string Name, string IndexUrl, string DistBaseUrl);
    private sealed record NodeDownloadRoute(
        NodeDownloadSource Source,
        HttpClient Client,
        string ConnectionName);
}

public sealed record CliInstallResult(bool Succeeded, string Message, string? Output);

public sealed record NodeInstallProgress(
    double Percent,
    string Message,
    string? Detail = null,
    bool CanCancel = true);
