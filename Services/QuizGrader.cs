namespace LearnSite.Services;

/// <summary>单题批改结果。ActualOutput 只在编程题中有值，便于学习者对照。</summary>
public sealed record QuestionResult(
    string Id,
    bool Correct,
    string Message,
    string? ActualOutput,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>批改选择题与编程题。编程题通过 CodeRunner 真正编译运行后再比较输出。</summary>
public sealed class QuizGrader
{
    private readonly CodeRunner _runner;

    public QuizGrader(CodeRunner runner) => _runner = runner;

    public QuestionResult GradeChoice(Question q, IReadOnlyCollection<int> selected)
    {
        var expected = q.Type == "multiple"
            ? q.Answer.EnumerateArray().Select(e => e.GetInt32()).OrderBy(x => x).ToArray()
            : new[] { q.Answer.GetInt32() };
        var got = selected.OrderBy(x => x).ToArray();
        bool ok = got.SequenceEqual(expected);

        string message = ok
            ? "回答正确。"
            : selected.Count == 0
                ? "还没有选择答案。"
                : "答案不对，再想想。";
        return new QuestionResult(q.Id, ok, message, null, []);
    }

    public async Task<QuestionResult> GradeCodeAsync(Question q, string code, CancellationToken ct = default)
    {
        var run = await _runner.RunAsync(code, ct);
        var errors = run.Diagnostics.Where(d => d.Severity == "error").ToList();

        if (!run.Built)
        {
            return new QuestionResult(q.Id, false,
                errors.Count > 0 ? "代码没有通过编译，请先看下方的错误信息。" : (run.Error ?? "编译失败。"),
                null, errors);
        }
        if (run.Error is not null)
        {
            return new QuestionResult(q.Id, false, run.Error, run.Stdout, run.Diagnostics);
        }
        if (run.ExitCode != 0)
        {
            return new QuestionResult(q.Id, false,
                $"程序以退出码 {run.ExitCode} 结束，请检查是否抛出了异常。",
                run.Stdout, run.Diagnostics);
        }

        bool ok = Normalize(run.Stdout) == Normalize(q.ExpectedOutput ?? "");
        return new QuestionResult(q.Id, ok,
            ok ? "输出与预期一致，通过。" : "输出与预期不一致，请对照题目要求再检查。",
            run.Stdout, run.Diagnostics);
    }

    /// <summary>统一换行、去掉每行行尾空白和首尾空行，便于比较。</summary>
    public static string Normalize(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim('\n');
    }
}
