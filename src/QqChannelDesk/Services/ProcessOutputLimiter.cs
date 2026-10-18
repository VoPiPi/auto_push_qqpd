using System.Diagnostics;
using System.IO;
using System.Text;

namespace QqChannelDesk.Services;

internal sealed class ProcessOutputLimitExceededException : Exception
{
    public ProcessOutputLimitExceededException(string streamName, int maxCharacters)
        : base($"{streamName}超过 {maxCharacters:N0} 个字符限制，已停止命令。")
    {
    }
}

internal readonly record struct ProcessOutputCapture(string StandardOutput, string StandardError);

/// <summary>
/// Reads process pipes concurrently and stops the process before either pipe can grow without bound.
/// </summary>
internal static class LimitedProcessOutputReader
{
    internal const int DefaultMaxCharactersPerStream = 2_000_000;

    internal static async Task<ProcessOutputCapture> ReadAsync(
        Process process,
        CancellationToken cancellationToken,
        int maxCharactersPerStream = DefaultMaxCharactersPerStream)
    {
        var stdoutTask = ReadToEndAsync(process.StandardOutput, "标准输出", maxCharactersPerStream);
        var stderrTask = ReadToEndAsync(process.StandardError, "标准错误", maxCharactersPerStream);
        var readersTask = Task.WhenAll(stdoutTask, stderrTask);
        var exitTask = process.WaitForExitAsync(cancellationToken);

        try
        {
            var completed = await Task.WhenAny(exitTask, readersTask).ConfigureAwait(false);
            if (completed == readersTask)
            {
                await readersTask.ConfigureAwait(false);
                await exitTask.ConfigureAwait(false);
            }
            else
            {
                await exitTask.ConfigureAwait(false);
                await readersTask.ConfigureAwait(false);
            }

            return new ProcessOutputCapture(await stdoutTask, await stderrTask);
        }
        catch (ProcessOutputLimitExceededException)
        {
            KillProcessTree(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<string> ReadToEndAsync(
        StreamReader reader,
        string streamName,
        int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0) break;
            if (builder.Length + read > maxCharacters)
                throw new ProcessOutputLimitExceededException(streamName, maxCharacters);
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static async Task DrainAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The process was killed; reader completion is best-effort.
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
