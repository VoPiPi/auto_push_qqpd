namespace QqChannelDesk.Services;

public enum DiagnosticState
{
    Unknown,
    Ready,
    Warning,
    Error
}

public sealed record DiagnosticItem(DiagnosticState State, string StateLabel, string Detail);

public sealed record DiagnosticReport(
    DiagnosticItem Node,
    DiagnosticItem Cli,
    DiagnosticItem Ffmpeg,
    DiagnosticItem Login,
    IReadOnlyList<string> LogLines,
    string OverallMessage);
