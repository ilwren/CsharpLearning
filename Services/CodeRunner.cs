using System.Diagnostics;
using Microsoft.JSInterop;

namespace LearnSite.Services;

/// <summary>
/// 编译与运行（浏览器端）。运行器是 wwwroot/runner 中的 .NET 程序，在 Web Worker 里执行，
/// 页面与它之间的调用经由 wwwroot/js/runner.js（window.ltRunner）。
/// 与旧的服务端版本相比：没有服务器进程，超时与停止都由 Worker 终止来完成。
/// </summary>
public sealed class CodeRunner
{
    private readonly IJSRuntime _js;

    public CodeRunner(IJSRuntime js) => _js = js;

    /// <summary>
    /// 编译的结果。编译通过时 Session 非空，程序已经开始运行，输出通过 Session.Events 读取。
    /// </summary>
    public sealed record StartOutcome(bool Built, string? Error, IReadOnlyList<Diagnostic> Diagnostics, RunSession? Session);

    /// <summary>交互式运行：编译并在后台运行，支持输入与停止。</summary>
    public async Task<StartOutcome> StartAsync(string code, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var session = new RunSession(_js);
        try
        {
            var reply = await _js.InvokeAsync<StartReply>("ltRunner.start", code, session.Reference);
            var diagnostics = ParseDiagnostics(reply.Text);
            if (!reply.Built)
            {
                await session.DisposeAsync();
                // 没有诊断却编译失败：通常是运行器本身出了问题，把第二行说明显示出来
                var detail = reply.Text.Split('\n') is { Length: > 1 } lines && diagnostics.Count == 0 ? lines[1] : null;
                return new StartOutcome(false, detail, diagnostics, null);
            }
            return new StartOutcome(true, null, diagnostics, session);
        }
        catch (JSException ex)
        {
            await session.DisposeAsync();
            return new StartOutcome(false, "运行器出错：" + ex.Message, [], null);
        }
    }

    /// <summary>批改用：不交互（读取输入得到 EOF），整个程序运行完才返回。超时由运行器强制停止。</summary>
    public async Task<RunResult> RunAsync(string code, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var reply = await _js.InvokeAsync<BatchReply>("ltRunner.batch", code);
        var diagnostics = ParseDiagnostics(reply.BuildText);
        if (!reply.Built)
        {
            return new RunResult(false, false, null, "", "", diagnostics, null, sw.Elapsed);
        }
        if (reply.TimedOut)
        {
            return new RunResult(true, false, null, reply.Output, "", diagnostics,
                "运行时间超过上限，已强制停止（是否有死循环？）", sw.Elapsed);
        }
        return new RunResult(true, true, reply.ExitCode, reply.Output, "", diagnostics, null, sw.Elapsed);
    }

    /// <summary>把编译器输出的每行诊断解析成对象。格式：severity \t line \t column \t code \t message。</summary>
    private static IReadOnlyList<Diagnostic> ParseDiagnostics(string text)
    {
        var list = new List<Diagnostic>();
        foreach (var line in text.Split('\n').Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 5) continue;
            int.TryParse(parts[1], out var row);
            int.TryParse(parts[2], out var col);
            list.Add(new Diagnostic(row, col, parts[0], parts[3], parts[4]));
        }
        return list;
    }

    // 与 wwwroot/js/runner.js 返回的对象对应（JS 侧为 camelCase，反序列化时不区分大小写）
    private sealed class StartReply
    {
        public bool Built { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class BatchReply
    {
        public bool Built { get; set; }
        public string BuildText { get; set; } = "";
        public string Output { get; set; } = "";
        public int? ExitCode { get; set; }
        public bool TimedOut { get; set; }
    }
}
