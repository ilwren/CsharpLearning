#!/usr/bin/env bash
# 构建浏览器端的 C# 运行器，并放进站点的 wwwroot：
#   wwwroot/runner/core/   交互式运行（Roslyn 编译器）
#   wwwroot/runner/tools/  批改与代码补全（Roslyn + Features + Workspaces）
#   wwwroot/refs/          编译用的 .NET 参考程序集（来自已安装 SDK 的 Microsoft.NETCore.App.Ref）
# 本地与 CI（GitHub Actions）共用。需要 .NET 10 SDK。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WWW="$ROOT/wwwroot"
BUILD="$ROOT/build"

# 每次从头发布：残留的中间产物（例如曾经裁剪过的框架程序集）会导致运行时报错
rm -rf "$BUILD" "$ROOT"/runner/*/obj "$ROOT"/runner/*/bin

echo "==> 发布 core"
dotnet publish "$ROOT/runner/Core/Core.csproj" -c Release -o "$BUILD/runner-core" --nologo -v:q

echo "==> 发布 tools"
dotnet publish "$ROOT/runner/Tools/Tools.csproj" -c Release -o "$BUILD/runner-tools" --nologo -v:q

rm -rf "$WWW/runner"
mkdir -p "$WWW/runner/core" "$WWW/runner/tools"
cp -r "$BUILD/runner-core/wwwroot/." "$WWW/runner/core/"
cp -r "$BUILD/runner-tools/wwwroot/." "$WWW/runner/tools/"
cp "$ROOT/runner/main.js" "$WWW/runner/core/main.js"
cp "$ROOT/runner/main.js" "$WWW/runner/tools/main.js"
# 去掉预压缩副本（.gz/.br）：托管方会按需压缩，且能减少一半上传量
find "$WWW/runner" \( -name "*.gz" -o -name "*.br" \) -delete

echo "==> 生成参考程序集 refs/"
PACK_ROOT="$(dotnet msbuild -getProperty:NetCoreTargetingPackRoot "$ROOT/runner/Core/Core.csproj" -nologo)"
REF_DIR="$PACK_ROOT/Microsoft.NETCore.App.Ref"
REF_VER="$(ls "$REF_DIR" | sort -V | tail -n 1)"
rm -rf "$WWW/refs"
mkdir -p "$WWW/refs"
cp "$REF_DIR/$REF_VER/ref/net10.0/"*.dll "$WWW/refs/"
python3 - "$WWW/refs" <<'PY'
import json, os, sys
d = sys.argv[1]
names = sorted(f for f in os.listdir(d) if f.endswith('.dll'))
with open(os.path.join(d, 'index.json'), 'w', encoding='utf-8') as f:
    json.dump(names, f)
print(f'参考程序集 {len(names)} 个')
PY

echo "==> 完成：$WWW/runner 与 $WWW/refs"
