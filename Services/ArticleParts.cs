using System.Text.RegularExpressions;

namespace LearnSite.Services;

/// <summary>正文的一段：普通 HTML，或者一组按平台分页的内容。</summary>
public abstract record ArticlePart;

public sealed record HtmlPart(string Html) : ArticlePart;

/// <summary>一个平台的页面内容。</summary>
public sealed record TabPage(string Platform, string Html);

public sealed record TabsPart(IReadOnlyList<TabPage> Pages) : ArticlePart;

/// <summary>
/// 按原文中的标记切分正文 HTML。标记由作者写在 Markdown 原文里，不由程序判断位置：
///   &lt;!--tab 组名 平台[,平台]--&gt;  其后的块属于这些平台，直到下一个标记
///   &lt;!--tab-end 组名--&gt;          结束该组
/// 组外的内容原样输出。同一块可以属于多个平台，会在每个平台页面里各出现一次。
/// </summary>
public static partial class ArticleSplitter
{
    [GeneratedRegex(@"<!--\s*tab(-end)?\s+(\S+)(?:\s+(\S+))?\s*-->")]
    private static partial Regex Marker();

    private static readonly string[] PlatformOrder = ["Windows", "macOS", "Linux"];

    public static IReadOnlyList<ArticlePart> Split(string html)
    {
        var parts = new List<ArticlePart>();
        var group = (Name: (string?)null, Items: new List<(string[] Plats, string Html)>());
        string[]? plats = null;     // 当前段落所属的平台（组内有效）
        int pos = 0;

        void Flush(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (group.Name is null)
            {
                parts.Add(new HtmlPart(text));
            }
            else if (plats is not null)
            {
                group.Items.Add((plats, text));
            }
        }

        void CloseGroup()
        {
            if (group.Name is not null)
            {
                parts.Add(BuildTabs(group.Items));
            }
            group = (null, new List<(string[] Plats, string Html)>());
            plats = null;
        }

        foreach (Match m in Marker().Matches(html))
        {
            Flush(html[pos..m.Index]);
            pos = m.Index + m.Length;

            if (m.Groups[1].Success)
            {
                // tab-end：只结束同名的组
                if (group.Name == m.Groups[2].Value) CloseGroup();
                continue;
            }

            var name = m.Groups[2].Value;
            if (group.Name is not null && group.Name != name) CloseGroup();
            if (group.Name is null) group = (name, new List<(string[] Plats, string Html)>());
            plats = m.Groups[3].Success
                ? m.Groups[3].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (plats.Length == 0) plats = null;
        }

        Flush(html[pos..]);
        CloseGroup();
        return parts;
    }

    private static ArticlePart BuildTabs(List<(string[] Plats, string Html)> items)
    {
        var order = new List<string>();
        foreach (var it in items)
        {
            foreach (var p in it.Plats)
            {
                if (!order.Contains(p)) order.Add(p);
            }
        }

        // 标签顺序统一：Windows、macOS、Linux 在前，其余按出现顺序排在后面
        order = order
            .OrderBy(p => Array.IndexOf(PlatformOrder, p) is var r && r >= 0 ? r : int.MaxValue)
            .ToList();

        if (order.Count < 2)
        {
            // 只有一个平台：没有分页的必要，直接输出内容
            return new HtmlPart(string.Concat(items.Select(i => i.Html)));
        }

        var pages = order
            .Select(p => new TabPage(p, string.Concat(items.Where(i => i.Plats.Contains(p)).Select(i => i.Html))))
            .ToList();
        return new TabsPart(pages);
    }
}
