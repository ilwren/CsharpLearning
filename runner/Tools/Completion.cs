using System.Collections.Immutable;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;

// 代码补全（和 IDE 用的是同一套 CompletionService）。仅 tools 程序包含。
// 每次请求把当前编辑器内容放进一个临时工作区，在光标位置求补全项。
// 返回值：每行一条，格式 label \t kind \t detail \t rank
public static partial class Runner
{
    static ImmutableArray<MetadataReference> _completionRefs = ImmutableArray<MetadataReference>.Empty;

    [JSExport]
    public static async Task<string> Complete(string code, int offset)
    {
        try
        {
            return await CompleteCore(code, offset);
        }
        catch (Exception)
        {
            // 补全失败不应影响编辑，返回空结果即可
            return "";
        }
    }

    static async Task<string> CompleteCore(string code, int offset)
    {
        if (References is null) return "";
        offset = Math.Clamp(offset, 0, code.Length);
        _completionRefs = References.ToImmutableArray();

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable);
        var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None);
        var info = ProjectInfo.Create(
            projectId, VersionStamp.Create(), "Play", "Play", LanguageNames.CSharp,
            compilationOptions: options,
            parseOptions: parse,
            metadataReferences: _completionRefs);

        var solution = workspace.CurrentSolution.AddProject(info);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "GlobalUsings.cs",
            SourceText.From(GlobalUsings));
        var programId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(programId, "Program.cs", SourceText.From(code));

        var document = solution.GetDocument(programId)!;
        var service = CompletionService.GetService(document);
        if (service is null) return "";

        var list = await service.GetCompletionsAsync(document, offset);
        if (list is null) return "";

        // Roslyn 返回的是未过滤的全部候选；IDE 再用光标前已输入的前缀过滤与排序（如输入 Le 时 Length 排在前面）
        IEnumerable<CompletionItem> items = list.ItemsList;
        var typed = code.Substring(list.Span.Start, Math.Max(0, offset - list.Span.Start));
        if (typed.Length > 0)
        {
            items = service.FilterItems(document, list.ItemsList.ToImmutableArray(), typed);
        }

        // 去重、截断，再把序号作为 rank 返回
        var sb = new StringBuilder();
        int rank = 0;
        foreach (var item in items
                     .Where(i => !i.DisplayText.Contains('<') && !string.IsNullOrWhiteSpace(i.DisplayText))
                     .GroupBy(i => i.DisplayText)
                     .Select(g => g.First())
                     .Take(200))
        {
            var detail = item.InlineDescription is { Length: > 0 } d ? d : "";
            sb.Append(Clean(item.DisplayText)).Append('\t')
              .Append(KindOf(item.Tags)).Append('\t')
              .Append(Clean(detail)).Append('\t')
              .Append(rank++).Append('\n');
        }
        return sb.ToString();
    }

    static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    static string KindOf(ImmutableArray<string> tags)
    {
        foreach (var t in tags)
        {
            switch (t)
            {
                case "Method": return "method";
                case "Class": return "class";
                case "Struct":
                case "Structure": return "struct";
                case "Interface": return "interface";
                case "Enum": return "enum";
                case "EnumMember": return "enummember";
                case "Field": return "field";
                case "Property": return "property";
                case "Event": return "event";
                case "Delegate": return "delegate";
                case "Namespace": return "namespace";
                case "Module": return "module";
                case "Keyword": return "keyword";
                case "Snippet": return "snippet";
                case "Local":
                case "Parameter":
                case "RangeVariable": return "variable";
                case "Constant": return "constant";
                case "TypeParameter": return "typeparameter";
            }
        }
        return "text";
    }
}
