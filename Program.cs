using LearnSite.Components;
using LearnSite.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

// 站点在浏览器中运行（Blazor WebAssembly），部署为纯静态文件（Cloudflare Pages）。
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// 所有请求都发往站点自身（与部署路径一致）
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddMudServices();

// 内容与题库：启动时读入目录与题库，章节按需加载
builder.Services.AddSingleton<ContentStore>();
builder.Services.AddSingleton<QuizGrader>();
// 编译运行（运行器在 Web Worker 中，见 wwwroot/js/runner.js）
builder.Services.AddSingleton<CodeRunner>();
// 每个浏览会话一份：记住当前选择的平台，同页的分页控件共用
builder.Services.AddScoped<PlatformState>();

var host = builder.Build();

// 渲染前先读入目录与题库（几十 KB），保证导航与首页可以同步使用
await host.Services.GetRequiredService<ContentStore>().InitAsync();

await host.RunAsync();
