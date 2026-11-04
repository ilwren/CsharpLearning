#!/usr/bin/env bash
# 构建完整站点（可直接部署到 Cloudflare Pages）：
#   1. 构建浏览器运行器（wwwroot/runner 与 wwwroot/refs）
#   2. 发布 Blazor WebAssembly 站点
# 输出目录：build/publish/wwwroot（部署时的根目录）。需要 .NET 10 SDK。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/publish"

bash "$ROOT/scripts/build-runner.sh"

echo "==> 发布站点"
rm -rf "$OUT"
dotnet publish "$ROOT/LearnSite.csproj" -c Release -o "$OUT" --nologo -v:q

for f in index.html _headers _redirects css/site.css js/runner.js js/site.js refs/index.json runner/core/main.js runner/tools/main.js; do
    if [ ! -f "$OUT/wwwroot/$f" ]; then
        echo "缺少 $OUT/wwwroot/$f" >&2
        exit 1
    fi
done

echo "==> 完成：$OUT/wwwroot"
echo "    文件数：$(find "$OUT/wwwroot" -type f | wc -l)，大小：$(du -sh "$OUT/wwwroot" | cut -f1)"
