using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// 运行在 Web Worker 中的 .NET 运行时（core 与 tools 两个程序共用这份代码）。
// 职责：用 Roslyn 编译用户代码，在同一个运行时中执行，并把输出、输入请求、诊断、结束状态交给 JS。
// 与页面之间的协议（JS 侧 lt.emit 收到的 kind 与 payload）：
//   build  : 第一行 OK 或 FAIL，之后每行一条诊断：severity \t line \t column \t code \t message
//   out    : 程序输出的一段文字
//   input  : 程序需要输入（由 JS 侧在等待前发出，这里不发）
//   exit   : 退出码（字符串）；未处理异常时退出码为 134
//   error  : 运行时内部错误的说明
public static partial class Runner
{
    public static void Main()
    {
        // 入口为空：由 JS 通过 [JSExport] 调用
    }

    [JSImport("globalThis.lt.emit")]
    internal static partial void Emit(string kind, string payload);

    // 阻塞式输入：JS 侧用 Atomics.wait 等待页面提供一行；返回 "\u0004" 表示输入已结束（相当于 Ctrl+D）
    [JSImport("globalThis.lt.readLine")]
    internal static partial string JsReadLine();

    internal static List<MetadataReference>? References;

    // 批改模式：没有交互，读取输入立即得到 EOF，不发出输入请求
    static bool _batch;

    // 与 .NET 控制台项目的 ImplicitUsings 一致，让示例代码无需手写常用 using
    const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    [JSExport]
    public static async Task<string> Init(string baseUrl)
    {
        // 参考程序集（Microsoft.NETCore.App.Ref）由站点的 refs/ 目录提供，编译时作为引用
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var names = ParseNames(await http.GetStringAsync("refs/index.json"));
        var list = new List<MetadataReference>(names.Count);
        foreach (var name in names)
        {
            var bytes = await http.GetByteArrayAsync("refs/" + name);
            list.Add(MetadataReference.CreateFromStream(new MemoryStream(bytes)));
        }
        References = list;
        return list.Count.ToString(CultureInfo.InvariantCulture);
    }

    [JSExport]
    public static void Run(string code, bool batch)
    {
        _batch = batch;
        try
        {
            RunCore(code);
        }
        catch (Exception ex)
        {
            Emit("error", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _batch = false;
        }
    }

    static void RunCore(string code)
    {
        if (References is null)
        {
            Emit("build", "FAIL");
            Emit("error", "运行器尚未初始化");
            return;
        }

        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(GlobalUsings, parse, path: "GlobalUsings.cs"),
            CSharpSyntaxTree.ParseText(code, parse, path: "Program.cs"),
        };
        var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable);
        var comp = CSharpCompilation.Create("User", trees, References, options);

        using var ms = new MemoryStream();
        var emit = comp.Emit(ms);
        var interesting = emit.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.SourceTree?.FilePath != "GlobalUsings.cs")
            .ToList();

        var sb = new StringBuilder(emit.Success ? "OK" : "FAIL");
        foreach (var d in interesting)
        {
            sb.Append('\n').Append(DiagnosticLine(d));
        }
        Emit("build", sb.ToString());
        if (!emit.Success) return;

        var asm = Assembly.Load(ms.ToArray());
        var entry = asm.EntryPoint!;

        // 注意：浏览器中 Console.In 的 getter 不受支持，这里不读取旧值，直接替换
        var oldOut = Console.Out;
        Console.SetOut(new JsWriter());
        Console.SetIn(new JsReader());
        int exitCode = 0;
        try
        {
            var args = entry.GetParameters().Length == 1 ? new object?[] { Array.Empty<string>() } : null;
            var result = entry.Invoke(null, args);
            exitCode = ExitCodeOf(result);
        }
        catch (TargetInvocationException ex)
        {
            Console.Out.Flush();
            var inner = ex.InnerException ?? ex;
            Emit("out", "未处理的异常：" + inner.GetType().FullName + ": " + inner.Message + "\n");
            exitCode = 134;
        }
        finally
        {
            Console.Out.Flush();
            Console.SetOut(oldOut);
        }
        Emit("exit", exitCode.ToString(CultureInfo.InvariantCulture));
    }

    // Main 返回 int 时作为退出码；返回 Task 时等待其完成，并取 Task<int> 的结果
    static int ExitCodeOf(object? result)
    {
        if (result is int i) return i;
        if (result is Task task)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new TargetInvocationException(ex.InnerException ?? ex);
            }
            var prop = task.GetType().GetProperty("Result");
            if (prop?.GetValue(task) is int code) return code;
        }
        return 0;
    }

    static string DiagnosticLine(Diagnostic d)
    {
        var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        int line = 0, column = 0;
        if (d.Location.IsInSource)
        {
            var span = d.Location.GetLineSpan().StartLinePosition;
            line = span.Line + 1;
            column = span.Character + 1;
        }
        var message = d.GetMessage(CultureInfo.InvariantCulture).Replace('\n', ' ').Replace('\t', ' ');
        return $"{severity}\t{line}\t{column}\t{d.Id}\t{message}";
    }

    internal static List<string> ParseNames(string json) =>
        json.Trim().Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('"'))
            .Where(s => s.Length > 0)
            .ToList();

    // 程序的标准输出：按行发送，减少消息数量
    sealed class JsWriter : TextWriter
    {
        readonly StringBuilder _buf = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            _buf.Append(value);
            if (value == '\n') Flush();
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            _buf.Append(value);
            if (value.Contains('\n')) Flush();
        }

        public override void Flush()
        {
            if (_buf.Length == 0) return;
            Emit("out", _buf.ToString());
            _buf.Clear();
        }
    }

    // 程序的标准输入：交互模式下阻塞等待页面；批改模式下直接返回 EOF
    sealed class JsReader : TextReader
    {
        string? _pending;
        int _pos;

        public override string? ReadLine()
        {
            if (_batch) return null;
            // 先把还没换行的提示语（例如 Console.Write("名字：")）送到页面，再等待输入
            Console.Out.Flush();
            var s = JsReadLine();
            return s == "\u0004" ? null : s;
        }

        public override int Read()
        {
            if (_pending is null || _pos >= _pending.Length)
            {
                var line = ReadLine();
                if (line is null) return -1;
                _pending = line + "\n";
                _pos = 0;
            }
            return _pending[_pos++];
        }
    }
}
