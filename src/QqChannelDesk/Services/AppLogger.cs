using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public sealed class AppLogger
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions ReadableJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Regex SensitiveText = new(
        "(?i)([\"']?(?:QQ_AI_CONNECT_TOKEN|access_token|refresh_token|authorization|cookie|qr_code|verification_uri|token|secret|password|session|ticket)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)",
        RegexOptions.Compiled);
    private const int MaxSessionEntries = 2000;
    private readonly List<(LogLevel Level, string Entry)> _sessionEntries = [];
    private Task _lastFileWrite = Task.CompletedTask;
    private Task _lastSettingsWrite = Task.CompletedTask;
    private readonly string _logDirectory;
    private readonly string _settingsPath;

    public static AppLogger Instance { get; } = new();

    public event Action<string>? EntryWritten;

    public bool DebugEnabled { get; private set; }
    public string LogDirectory => _logDirectory;

    public AppLogger(string? rootDirectory = null)
    {
        var settingsRoot = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqChannelDesk");
        _settingsPath = Path.Combine(settingsRoot, "settings.json");
        _logDirectory = Path.Combine(rootDirectory ?? AppContext.BaseDirectory, "logs");
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                DebugEnabled = settings.RootElement.TryGetProperty("debugEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugEnabled = false;
        }
    }

    public void SetDebugEnabled(bool enabled)
    {
        DebugEnabled = enabled;
        var settingsPath = _settingsPath;
        _lastSettingsWrite = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { debugEnabled = enabled }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write("无法保存调试模式设置。", LogLevel.Warning, detail: false);
            }
        });
    }

    private void WriteLogFile(string entry)
    {
        var logDirectory = _logDirectory;
        var fileName = $"{DateTime.Now:yyyy-MM-dd}.log";
        var previousWrite = _lastFileWrite;
        _lastFileWrite = Task.Run(async () =>
        {
            try
            {
                await previousWrite;
                Directory.CreateDirectory(logDirectory);
                lock (FileLock)
                {
                    File.AppendAllText(Path.Combine(logDirectory, fileName), entry + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    public void Info(string message) => Write(message, LogLevel.Info, detail: false);
    public void Warning(string message) => Write(message, LogLevel.Warning, detail: false);
    public void Error(string message) => Write(message, LogLevel.Error, detail: false);
    public void Debug(string message) => Write(message, LogLevel.Debug, detail: true);

    public void CliResult(string operation, int? exitCode, string standardOutput, string standardError)
    {
        var details = $"CLI {operation}，退出码：{exitCode?.ToString() ?? "无"}";
        if (!string.IsNullOrWhiteSpace(standardOutput)) details += $"\nstdout:\n{Redact(standardOutput)}";
        if (!string.IsNullOrWhiteSpace(standardError)) details += $"\nstderr:\n{Redact(standardError)}";
        Write($"{operation}{(exitCode == 0 ? "完成" : "未成功")}", exitCode == 0 ? LogLevel.Info : LogLevel.Warning);
        Write(details, LogLevel.Debug, detail: true);
    }

    public void Write(string message, LogLevel level = LogLevel.Info, bool detail = false)
    {
        var safe = Redact(message ?? string.Empty);
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {safe}";
        WriteLogFile(entry);

        AddSessionEntry(level, entry);

        if (DebugEnabled || level != LogLevel.Debug) EntryWritten?.Invoke(RedactErrorCodes(entry));
    }

    private void AddSessionEntry(LogLevel level, string entry)
    {
        lock (FileLock)
        {
            _sessionEntries.Add((level, entry));
            if (_sessionEntries.Count > MaxSessionEntries)
                _sessionEntries.RemoveRange(0, _sessionEntries.Count - MaxSessionEntries);
        }
    }

    public IReadOnlyList<string> ReadEntries(bool includeDebug)
    {
        lock (FileLock)
        {
            return _sessionEntries
                .Where(item => includeDebug || item.Level != LogLevel.Debug)
                .Select(item => RedactErrorCodes(item.Entry))
                .ToArray();
        }
    }

    public async Task FlushAsync()
    {
        await _lastSettingsWrite;
        await _lastFileWrite;
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(RedactElement(document.RootElement), ReadableJsonOptions);
        }
        catch (JsonException)
        {
            var safe = SensitiveText.Replace(text, "$1=[已隐藏]");
            safe = Regex.Replace(safe, "(?i)bot:v1_[A-Za-z0-9._-]+", "[凭证已隐藏]");
            safe = Regex.Replace(safe, "(?i)(Bearer\\s+)[A-Za-z0-9._~-]+", "$1[已隐藏]");
            return safe;
        }
    }

    private static object? RedactElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => IsSensitiveKey(property.Name) ? "[已隐藏]" : RedactElement(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(RedactElement).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.Clone(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static bool IsSensitiveKey(string key) => Regex.IsMatch(key,
        "(?i)(token|authorization|cookie|qr|verification|credential|secret|password|session|ticket)");

    private static bool IsErrorCodeKey(string key) => Regex.IsMatch(key,
        "(?i)^(?:code|error_code|errorcode|err_code|errcode|ret_code|retcode|errno|error_no)$");

    private static string RedactErrorCodes(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(RedactErrorCodeElement(document.RootElement), ReadableJsonOptions);
        }
        catch (JsonException)
        {
            return Regex.Replace(text,
                "(?i)([\"']?(?:error_code|errorcode|err_code|errcode|ret_code|retcode|errno|error_no|code)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)",
                "$1[已隐藏]");
        }
    }

    private static object? RedactErrorCodeElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => IsErrorCodeKey(property.Name) ? "[已隐藏]" : RedactErrorCodeElement(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(RedactErrorCodeElement).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.Clone(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public enum LogLevel { Debug, Info, Warning, Error }
}
