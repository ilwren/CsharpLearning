# C# 自学教程（网站）

一个从零开始学 C# 的在线教程站点：章节阅读、代码块复制与高亮、可直接在浏览器中编译运行的编辑器（支持输入与补全）、每章的自测与编程题批改。全部在浏览器中运行，没有后端。

- 站点：Blazor WebAssembly + MudBlazor + Markdig
- 代码运行：Roslyn 与 .NET 运行时在 Web Worker 中运行（`runner/`）
- 部署：Cloudflare Pages，见 [DEPLOY.md](DEPLOY.md)

## 快速开始

需要 .NET 10 SDK 与 Python 3。

```bash
./scripts/build-site.sh     # 构建（输出 build/publish/wwwroot）
python3 scripts/serve.py    # 预览 http://localhost:8080
```

## 目录

```
Components/        页面与组件（ChapterView、QuizPanel、RunPanel、CodeEditor……）
Services/          内容加载（ContentStore）、编译运行（CodeRunner / RunSession）、批改（QuizGrader）
runner/            浏览器内的 .NET 运行器（Core：交互运行；Tools：批改与补全；Shared：共用代码）
scripts/           build-runner.sh、build-site.sh、serve.py
tools/             内容与题库的生成脚本（sync_content.py、build_quizzes.py、quiz_source.py）
wwwroot/
  content/         章节 Markdown、题库 JSON、index.json、links.json、插图（已提交）
  js/              runner.js（Worker 管理）、site.js（编辑器与页面增强）
  css/site.css     样式
  lib/monaco/      Monaco 编辑器（已提交）
  _headers         响应头（跨源隔离）
  _redirects       单页应用路由
```

`wwwroot/runner/`、`wwwroot/refs/` 与 `build/` 由构建生成，不提交。

## 发布范围

当前收录第 0–15 章与对应题库。第 16 章之后与附录尚未写完，暂不发布；由 `tools/sync_content.py` 的 `MAX_CHAPTER` 控制。

## 修改题库

只改 `tools/quiz_source.py`，然后运行 `python3 tools/build_quizzes.py --verify`。

## 许可与来源

章节内容改编自 LogLens 教程原文。插图为 Material Design 风格的重绘 SVG（深浅两套）；第 0 章的一幅差分机插图取自 Harper's 1864（公有领域）；它与正文的对应关系是推断，发布前请核对。
