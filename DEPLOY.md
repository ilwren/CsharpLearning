# 部署说明

本站是纯静态站点：Blazor WebAssembly 在浏览器中运行，代码的编译与运行也在浏览器里完成（Web Worker 中的 .NET 运行时 + Roslyn），**没有后端服务器**。部署目标是 Cloudflare Pages，由 GitHub Actions 构建并发布。

## 一、架构

| 部分 | 位置 | 说明 |
|---|---|---|
| 站点 | `Components/`、`Services/`、`Program.cs`、`LearnSite.csproj` | Blazor WebAssembly（已裁剪，只含站点用到的框架成员） |
| 运行器 | `runner/`（`Core/` 交互运行，`Tools/` 批改与补全，`Shared/RunnerCore.cs` 共用） | 构建后放在 `wwwroot/runner/`，在 Web Worker 中运行，不裁剪 |
| 参考程序集 | 构建时从 .NET SDK 复制到 `wwwroot/refs/` | 编译用户代码需要，不提交 |
| 前端脚本 | `wwwroot/js/runner.js`（Worker 管理）、`wwwroot/js/site.js`（编辑器、代码块、分页） | 普通脚本，先于 Blazor 加载 |
| 内容 | `wwwroot/content/`（章节 Markdown、题库 JSON、`index.json`、`links.json`、插图） | 由 `tools/` 生成，已提交 |
| 响应头 | `wwwroot/_headers` | 跨源隔离：`COOP: same-origin`、`COEP: require-corp` |
| 路由 | `wwwroot/_redirects` | `/* /index.html 200`，单页应用 |

### 为什么需要跨源隔离

交互式输入要求运行器在 Worker 中**阻塞等待**页面提供输入（`Console.ReadLine`），这需要 `SharedArrayBuffer` 与 `Atomics.wait`。浏览器只在跨源隔离的页面中提供它们，所以必须有 `_headers` 中的两个响应头。Google Fonts 的样式表已加 `crossorigin` 属性以兼容 COEP。

### 运行器的裁剪策略

- **站点**：启用 Blazor 默认的 IL 裁剪（只裁站点未使用的框架成员）。已实测：站点全部功能测试通过。
- **运行器**：**不裁剪**。实测裁剪后用户程序会因找不到框架成员而失败（6 个测试程序全部报 `Method not found`）。
- **ReadyToRun**：在 browser-wasm 目标上 SDK 不支持（NETSDK1095），不可用。

## 二、构建

需要 .NET 10 SDK（本地已用 10.0.403 验证）与 Python 3（仅用于生成参考程序集清单与本地预览）。

```bash
./scripts/build-site.sh          # 输出 build/publish/wwwroot，即部署目录
python3 scripts/serve.py         # 本地预览（带跨源隔离头），http://localhost:8080
```

`build-site.sh` 先运行 `build-runner.sh`（发布两个运行器、生成 `refs/`），再发布站点。全量构建约 2.5 分钟。

**不要用 `dotnet run` 预览**：它不带跨源隔离头，运行器会不可用。

## 三、部署到 Cloudflare Pages

### 1. 创建 Pages 项目（一次性）

在 Cloudflare 控制台 → Workers & Pages → Create → Pages，或用命令行：

```bash
npx wrangler pages project create csharp-learning --production-branch main
```

项目名要与 `.github/workflows/deploy.yml` 中的 `PROJECT_NAME` 一致。

### 2. 配置 GitHub Secrets

在仓库 Settings → Secrets and variables → Actions 中添加：

- `CLOUDFLARE_API_TOKEN`：令牌权限选择 *Account · Cloudflare Pages · Edit*。
- `CLOUDFLARE_ACCOUNT_ID`：Cloudflare 账户 ID。

### 3. 推送

推送到 `main` 分支会触发 `.github/workflows/deploy.yml`：安装 .NET 10 → `build-site.sh` → `wrangler pages deploy build/publish/wwwroot`。也可以在 Actions 页面手动运行（workflow_dispatch）。

> 工作流中的 `cloudflare/wrangler-action@v3`、`actions/setup-dotnet@v4` 为常用版本，首次运行前请在 Actions 页面确认没有版本过期的提示。

## 四、更新内容

内容的源文件（LogLens 教程的 Markdown 原文）不在本仓库中。维护流程：

1. 在 LogLens 工作区修改 `tutorial/` 下的原文。
2. 修改题库只改 `tools/quiz_source.py`，然后运行：
   ```bash
   python3 tools/build_quizzes.py --verify   # 生成题库，并编译运行每道编程题的参考答案
   ```
3. 同步章节与插图（需要 `../tutorial` 目录存在，即仓库与 `tutorial/` 同级）：
   ```bash
   python3 tools/sync_content.py
   ```
   会更新 `wwwroot/content/chapters/`、`wwwroot/content/images/` 与 `index.json`。
4. 提交 `wwwroot/content/` 的变更并推送。

`links.json`（延伸阅读）直接编辑 `wwwroot/content/links.json`。

**发布范围**：当前只发布第 0–15 章（含题库）。第 16 章之后与附录 A–D 尚未写完，`tools/sync_content.py` 中的 `MAX_CHAPTER = 15` 控制范围。写完并审阅后，调高该值、补充题库、重新同步即可；同步脚本会删除不再收录的旧副本和未被引用的图片。正文中“第 18 章”之类的提及在未收录时显示为普通文字，不会变成死链。

**推荐学习时长**：章节页显示的时长 = 阅读时长（按字数与代码行估算）+ 每章“动手试一试”10 分钟 + 每道选择题 2 分钟 + 每道编程题 10 分钟，向上取整到 5 分钟。这些是经验估计，参数在 `Services/ContentStore.cs` 的 `PlaygroundMinutes`、`ChoiceQuestionMinutes`、`CodeQuestionMinutes` 中。

## 五、已知限制（请在上线前确认）

1. **第 12、13 章的部分示例在网页中不能运行。** 运行器运行在单线程的 WebAssembly 环境中：
   - `Task.Delay`（含 `await Task.Delay`）报 `Cannot wait on monitors on this runtime`；第 12 章 41 处、第 13 章相关示例受影响。
   - `new Thread(...).Start()` 报 `Arg_PlatformNotSupported`；仅第 13 章 10 处。
   - 可用：`Task.Run` + `Result`/`Wait()`、`Thread.Sleep`、`lock`、`Parallel.For`、`Interlocked`、`Monitor`、`Task.WaitAll`、`CancellationTokenSource`。
   - 已决定（方案 a）：接受这些示例在网页中不可运行，正文不改、不加提示。原定的候选方案为"需在本机运行"的提示（见 `wwwroot/content/chapters/12.md`、`13.md`）。
2. **首次加载体积。** 站点本体约 3.3 MB（gzip）；第一次点击运行时加载运行器约 12.5 MB（gzip）；第一次补全或批改时加载约 17.8 MB（gzip，包含 Features）。之后由浏览器缓存。
3. **浏览器要求。** 需要支持 WebAssembly、Web Worker、`SharedArrayBuffer` 与模块 Worker 的现代浏览器。移动端可以浏览，但编辑器与运行体验受限。
4. **不存在的路由返回 200。** `_redirects` 把未知路径回退到 `index.html`，由 Blazor 显示"找不到"页面，HTTP 状态码仍是 200。

## 六、排错

- 运行器一直"编译中…"：检查响应头（DevTools → Network → 文档的 `Cross-Origin-Embedder-Policy`）；确认是通过 `serve.py` 或 Pages 访问，而不是 `dotnet run`。
- 补全无结果：补全与批改共用 `tools` 运行器，首次加载较慢（数秒），属正常现象。
- 构建时找不到 `NetCoreTargetingPackRoot` 下的参考程序集：确认 SDK 为 10.x 且包含 `packs/Microsoft.NETCore.App.Ref`。
