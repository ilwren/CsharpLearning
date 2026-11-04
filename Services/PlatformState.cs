namespace LearnSite.Services;

/// <summary>
/// 当前浏览会话选择的平台（Windows / macOS / Linux）。
/// 同一页面上的所有分页控件共用这个状态，切换一处，其余同步切换。
/// 初始值由浏览器的记录或系统类型决定（见 PlatformTabs 组件）。
/// </summary>
public sealed class PlatformState
{
    public string? Current { get; private set; }

    public event Action? Changed;

    /// <summary>只在还没有选择时设置初始值，不触发通知。</summary>
    public void Init(string platform)
    {
        Current ??= platform;
    }

    public void Set(string platform)
    {
        if (Current == platform) return;
        Current = platform;
        Changed?.Invoke();
    }
}
