using System.Threading.Channels;
using Microsoft.JSInterop;

namespace LearnSite.Services;

/// <summary>输出段的种类，决定显示的颜色。</summary>
public enum SegmentKind
{
    Output,
    Input,
    Error,
    System,
}

/// <summary>运行过程中的事件（由 RunPanel 逐个读取并显示）。</summary>
public abstract record RunEvent;

/// <summary>程序输出了一段文字（或回显了学习者的输入）。</summary>
public sealed record OutputEvent(SegmentKind Kind, string Text) : RunEvent;

/// <summary>程序在等待输入（Console.ReadLine）：页面此时显示输入框。</summary>
public sealed record InputRequestedEvent : RunEvent;

/// <summary>程序结束。ExitCode 为 null 表示被强制停止（超时）；TimedOut 为 true 表示超过运行时间上限。</summary>
public sealed record ExitedEvent(int? ExitCode, bool TimedOut) : RunEvent;

/// <summary>
/// 一次交互式运行的会话（浏览器端）。运行器在 Web Worker 中执行；
/// 它通过 JS（wwwroot/js/runner.js）把事件回传到这里：OnEvent。
/// </summary>
public sealed class RunSession : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private bool _ended;

    public RunSession(IJSRuntime js)
    {
        _js = js;
        Reference = DotNetObjectReference.Create(this);
    }

    /// <summary>传给 JS 的回调引用。</summary>
    public DotNetObjectReference<RunSession> Reference { get; }

    /// <summary>事件流：读到 ExitedEvent 后结束。</summary>
    public ChannelReader<RunEvent> Events => _events.Reader;

    /// <summary>程序是否正在等待输入。</summary>
    public bool AwaitingInput { get; private set; }

    /// <summary>JS 调用的入口。kind：out / input / exit / timeout。</summary>
    [JSInvokable]
    public void OnEvent(string kind, string text, int code)
    {
        if (_ended) return;
        switch (kind)
        {
            case "out":
                _events.Writer.TryWrite(new OutputEvent(SegmentKind.Output, text));
                break;
            case "input":
                AwaitingInput = true;
                _events.Writer.TryWrite(new InputRequestedEvent());
                break;
            case "exit":
                End(new ExitedEvent(code, false));
                break;
            case "timeout":
                End(new ExitedEvent(null, true));
                break;
        }
    }

    private void End(RunEvent ev)
    {
        if (_ended) return;
        _ended = true;
        AwaitingInput = false;
        _events.Writer.TryWrite(ev);
        _events.Writer.TryComplete();
    }

    /// <summary>发送一行输入（同时在输出中回显）。</summary>
    public async Task SendLineAsync(string line)
    {
        if (_ended || !AwaitingInput) return;
        AwaitingInput = false;
        _events.Writer.TryWrite(new OutputEvent(SegmentKind.Input, line + "\n"));
        await _js.InvokeVoidAsync("ltRunner.sendInput", line);
    }

    /// <summary>结束输入（相当于 Ctrl+D），之后 ReadLine 返回 null。</summary>
    public async Task CloseInputAsync()
    {
        if (_ended || !AwaitingInput) return;
        AwaitingInput = false;
        await _js.InvokeVoidAsync("ltRunner.closeInput");
    }

    /// <summary>停止运行（终止 Worker）。</summary>
    public Task StopAsync()
    {
        if (_ended) return Task.CompletedTask;
        return _js.InvokeVoidAsync("ltRunner.stop").AsTask();
    }

    /// <summary>不等待结果地停止运行；用于重置或离开页面。</summary>
    public void Kill()
    {
        if (_ended) return;
        _ = StopAsync().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public ValueTask DisposeAsync()
    {
        Reference.Dispose();
        return ValueTask.CompletedTask;
    }
}
