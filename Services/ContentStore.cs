using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Markdig;

namespace LearnSite.Services;

/// <summary>目录里的一项。Key 是内部标识：章用 "07"，附录用 "appendix-A"。</summary>
public sealed record ChapterInfo(string Key, string Title, string Part, string Url);

/// <summary>题库：一章对应一个 JSON 文件，位于 wwwroot/content/quizzes 下。</summary>
public sealed class Quiz
{
    public string Chapter { get; set; } = "";
    public List<Question> Questions { get; set; } = [];
}

/// <summary>
/// 一道题。Type 取值：
///   single    单选，Answer 为选项下标（整数）
///   multiple  多选，Answer 为下标数组
///   code      编程题，学习者写代码，运行后与 ExpectedOutput 比较（忽略行尾空白）
/// </summary>
public sealed class Question
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "single";
    public string Prompt { get; set; } = "";
    public List<string>? Options { get; set; }
    public JsonElement Answer { get; set; }
    public string? Starter { get; set; }
    [JsonPropertyName("expected")]
    public string? ExpectedOutput { get; set; }
    public string? Hint { get; set; }
    public string? Explain { get; set; }
}

/// <summary>章节的阅读信息。Chars 为正文字数（汉字 + 英文单词，不含代码）。</summary>
public sealed record ReadingStats(int Chars, int Minutes);

/// <summary>延伸阅读的一条链接（官方文档等）。</summary>
public sealed record LinkItem(string Title, string Url);

/// <summary>content/index.json 的结构：有序的章节列表与有题库的章号。由 tools/sync_content.py 生成。</summary>
public sealed class ContentIndex
{
    public List<IndexChapter> Chapters { get; set; } = [];
    public List<string> Quizzes { get; set; } = [];
}

public sealed class IndexChapter
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// 站点内容（浏览器内加载）。启动时读取目录、延伸阅读与全部题库（体积很小）；
/// 章节 Markdown 按需加载，Markdig 渲染与字数统计在首次打开该章时完成。
/// </summary>
public sealed class ContentStore
{
    private static readonly Regex CjkChar = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex LatinWord = new(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);
    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoIdentifiers()
        .Build();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // 推荐学习时长的估计参数（分钟）。这是经验估计，不是测量值；只在这里调整。
    public const int PlaygroundMinutes = 10;      // 每章“动手试一试”
    public const int ChoiceQuestionMinutes = 2;   // 每道选择题（单选、多选）
    public const int CodeQuestionMinutes = 10;    // 每道编程题

    private readonly HttpClient _http;
    private readonly Dictionary<string, ChapterInfo> _info = new();
    private readonly List<ChapterInfo> _ordered = [];
    private readonly Dictionary<string, List<LinkItem>> _links = new();
    private readonly Dictionary<string, Quiz> _quizzes = new();
    private readonly Dictionary<string, IReadOnlyList<ArticlePart>> _parts = new();
    private readonly Dictionary<string, ReadingStats> _stats = new();
    private readonly Dictionary<string, Task> _loading = new();

    public ContentStore(HttpClient http) => _http = http;

    public IReadOnlyList<ChapterInfo> Ordered => _ordered;

    /// <summary>启动时调用一次：读取目录、延伸阅读与全部题库。</summary>
    public async Task InitAsync()
    {
        var index = await _http.GetFromJsonAsync<ContentIndex>("content/index.json", JsonOptions)
                    ?? throw new InvalidDataException("content/index.json 无法读取");

        foreach (var c in index.Chapters)
        {
            var info = BuildInfo(c.Key, c.Title);
            _info[c.Key] = info;
            _ordered.Add(info);
        }

        var links = await _http.GetFromJsonAsync<Dictionary<string, List<LinkItem>>>("content/links.json", JsonOptions);
        if (links is not null)
        {
            foreach (var (key, items) in links) _links[key] = items;
        }

        var quizzes = await Task.WhenAll(index.Quizzes.Select(async key =>
        {
            var quiz = await _http.GetFromJsonAsync<Quiz>($"content/quizzes/{key}.json", JsonOptions)
                       ?? throw new InvalidDataException($"题库 {key} 无法解析");
            return (key, quiz);
        }));
        foreach (var (key, quiz) in quizzes) _quizzes[key] = quiz;
    }

    public ChapterInfo? Find(string key) => _info.TryGetValue(key, out var c) ? c : null;

    /// <summary>确保某章的正文已加载并渲染。页面切换章节时调用。</summary>
    public Task EnsureChapterAsync(string key)
    {
        if (!_info.ContainsKey(key) || _parts.ContainsKey(key)) return Task.CompletedTask;
        if (!_loading.TryGetValue(key, out var task))
        {
            task = LoadChapterAsync(key);
            _loading[key] = task;
        }
        return task;
    }

    private async Task LoadChapterAsync(string key)
    {
        try
        {
            var markdown = await _http.GetStringAsync($"content/chapters/{key}.md");
            var html = Markdig.Markdown.ToHtml(markdown, Pipeline);
            _stats[key] = ComputeStats(markdown);
            // 正文按原文标记切分后的各段（普通 HTML 或平台分页），由 Blazor 逐段渲染
            _parts[key] = ArticleSplitter.Split(html);
        }
        finally
        {
            _loading.Remove(key);
        }
    }

    /// <summary>正文切分后的各段；未加载时为空，需先 EnsureChapterAsync。</summary>
    public IReadOnlyList<ArticlePart> GetParts(string key) => _parts.TryGetValue(key, out var p) ? p : [];

    public Quiz? GetQuiz(string key) => _quizzes.TryGetValue(key, out var q) ? q : null;

    /// <summary>
    /// 推荐学习时长（分钟，向上取整到 5 分钟）：阅读时长 + 动手试一试 + 自测题的估计时间。
    /// 未加载时为 null，需先 EnsureChapterAsync。
    /// </summary>
    public int? GetStudyMinutes(string key)
    {
        var stats = GetStats(key);
        if (stats is null) return null;
        var practice = PlaygroundMinutes;
        if (GetQuiz(key) is { } quiz)
        {
            foreach (var q in quiz.Questions)
            {
                practice += q.Type == "code" ? CodeQuestionMinutes : ChoiceQuestionMinutes;
            }
        }
        return (int)Math.Ceiling((stats.Minutes + practice) / 5.0) * 5;
    }

    /// <summary>阅读信息；未加载时为 null，需先 EnsureChapterAsync。</summary>
    public ReadingStats? GetStats(string key) => _stats.TryGetValue(key, out var s) ? s : null;

    public IReadOnlyList<LinkItem> GetLinks(string key) =>
        _links.TryGetValue(key, out var l) ? l : [];

    /// <summary>统计字数与推荐阅读时长。代码围栏内的行只计入代码行数，不计入字数。</summary>
    private static ReadingStats ComputeStats(string markdown)
    {
        var prose = new StringBuilder();
        int codeLines = 0;
        bool fence = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }
            if (fence)
            {
                codeLines++;
                continue;
            }
            var t = line.TrimStart();
            if (t.Length == 0 || t.StartsWith('<') || t.StartsWith("![", StringComparison.Ordinal)
                || t.StartsWith("|", StringComparison.Ordinal))
            {
                continue;
            }
            prose.AppendLine(MdLink.Replace(line, "$1"));
        }

        var text = prose.ToString();
        int cjk = CjkChar.Matches(text).Count;
        int latin = LatinWord.Matches(text).Count;
        double minutes = cjk / 300.0 + latin / 200.0 + codeLines / 25.0;
        int rounded = Math.Max(5, (int)Math.Ceiling(minutes / 5.0) * 5);
        return new ReadingStats(cjk + latin, rounded);
    }

    /// <summary>上一章、下一章（按目录顺序）。</summary>
    public (ChapterInfo? Prev, ChapterInfo? Next) Neighbors(string key)
    {
        var i = _ordered.FindIndex(c => c.Key == key);
        if (i < 0) return (null, null);
        return (i > 0 ? _ordered[i - 1] : null, i < _ordered.Count - 1 ? _ordered[i + 1] : null);
    }

    /// <summary>章节所属的篇（用于左侧导航分组）。</summary>
    public static string PartOf(string key)
    {
        if (key.StartsWith("appendix", StringComparison.Ordinal)) return "附录";
        if (!int.TryParse(key, out var n)) return "其它";
        return n switch
        {
            <= 15 => "第一篇 · C# 语言",
            <= 21 => "第二篇 · LogLens 控制台",
            _ => "第三篇 · 图形界面",
        };
    }

    private static ChapterInfo BuildInfo(string key, string title)
    {
        var part = PartOf(key);
        var url = key.StartsWith("appendix-", StringComparison.Ordinal)
            ? $"/appendix/{key["appendix-".Length..]}"
            : $"/chapter/{key}";
        return new ChapterInfo(key, title, part, url);
    }
}
