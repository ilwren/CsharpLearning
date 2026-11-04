using Microsoft.JSInterop;

namespace LearnSite.Services;

/// <summary>章节页面里的代码块按钮（“在编辑器中打开”）通过它回调到 Blazor。</summary>
public sealed class ChapterBridge
{
    private readonly Func<string, Task> _open;

    public ChapterBridge(Func<string, Task> open) => _open = open;

    [JSInvokable]
    public Task OpenBlock(string code) => _open(code);
}
