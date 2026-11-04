namespace LearnSite.Services;

/// <summary>编译器给出的一条诊断信息。Line/Column 从 1 开始；Line 为 0 表示不在某一行上。</summary>
public sealed record Diagnostic(int Line, int Column, string Severity, string Code, string Message);

/// <summary>一次“编译并运行”的结果。</summary>
public sealed record RunResult(
    bool Built,
    bool Ran,
    int? ExitCode,
    string Stdout,
    string Stderr,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? Error,
    TimeSpan Duration);
