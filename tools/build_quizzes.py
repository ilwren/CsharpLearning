#!/usr/bin/env python3
"""从 tools/quiz_source.py 生成 wwwroot/content/quizzes/*.json，并可选地验证题目。

用法：
  python3 tools/build_quizzes.py            # 生成 JSON（不含参考答案）
  python3 tools/build_quizzes.py --verify   # 生成，并真正编译运行每道编程题的参考答案，核对预期输出

验证需要 dotnet SDK（会在临时目录里创建控制台项目，不占用工作空间）。
"""
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

SITE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from quiz_source import QUIZZES  # noqa: E402

OUT = SITE / "wwwroot" / "content" / "quizzes"
VERIFY_DIR = Path("/tmp/learnsite-quiz-verify")

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <LangVersion>latest</LangVersion>
    <UseAppHost>false</UseAppHost>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" /></ItemGroup>
</Project>
"""


def normalize(text: str) -> str:
    lines = [ln.rstrip() for ln in text.replace("\r\n", "\n").split("\n")]
    return "\n".join(lines).strip("\n")


def public_question(q: dict) -> dict:
    """网站使用的题目：去掉参考答案。"""
    out = {k: v for k, v in q.items() if k != "solution"}
    return out


def check_choices(chapter: str, q: dict, problems: list[str]) -> None:
    if q["type"] in ("single", "multiple"):
        opts = q.get("options") or []
        answers = q["answer"] if isinstance(q["answer"], list) else [q["answer"]]
        if len(opts) < 2:
            problems.append(f"{q['id']}: 选项不足")
        if any(a < 0 or a >= len(opts) for a in answers):
            problems.append(f"{q['id']}: 答案下标越界")
        if q["type"] == "single" and isinstance(q["answer"], list):
            problems.append(f"{q['id']}: 单选题答案应为单个下标")
        if q["type"] == "multiple" and not isinstance(q["answer"], list):
            problems.append(f"{q['id']}: 多选题答案应为列表")
    elif q["type"] == "code":
        if "solution" not in q or "expected" not in q:
            problems.append(f"{q['id']}: 编程题缺少 solution 或 expected")
    else:
        problems.append(f"{q['id']}: 未知题型 {q['type']}")


def run_solution(code: str) -> tuple[bool, str, str]:
    """编译并运行参考答案，返回 (是否编译通过, 标准输出, 错误信息)。"""
    VERIFY_DIR.mkdir(parents=True, exist_ok=True)
    (VERIFY_DIR / "LearnVerify.csproj").write_text(CSPROJ, encoding="utf-8")
    (VERIFY_DIR / "Program.cs").write_text(code, encoding="utf-8")
    build = subprocess.run(
        ["dotnet", "build", str(VERIFY_DIR / "LearnVerify.csproj"), "-nologo", "-v", "q",
         "-consoleLoggerParameters:NoSummary"],
        capture_output=True, text=True, timeout=300)
    if build.returncode != 0:
        errors = [ln.strip() for ln in (build.stdout + build.stderr).splitlines() if " error " in ln]
        return False, "", "\n".join(dict.fromkeys(errors))
    dll = VERIFY_DIR / "bin" / "Debug" / "net10.0" / "LearnVerify.dll"
    run = subprocess.run(["dotnet", str(dll)], capture_output=True, text=True, timeout=60)
    return True, run.stdout, run.stderr


def main() -> None:
    verify = "--verify" in sys.argv
    problems: list[str] = []

    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.json"):
        old.unlink()

    for chapter, questions in QUIZZES.items():
        ids = [q["id"] for q in questions]
        if len(set(ids)) != len(ids):
            problems.append(f"第 {chapter} 章题号重复")
        for q in questions:
            check_choices(chapter, q, problems)
        data = {"chapter": chapter, "questions": [public_question(q) for q in questions]}
        (OUT / f"{chapter}.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"已生成 {len(QUIZZES)} 章题库 -> {OUT}")

    if verify:
        passed = failed = 0
        for chapter, questions in QUIZZES.items():
            for q in questions:
                if q["type"] != "code":
                    continue
                ok_build, stdout, stderr = run_solution(q["solution"])
                if not ok_build:
                    failed += 1
                    problems.append(f"{q['id']}: 参考答案编译失败：{stderr[:300]}")
                    continue
                if normalize(stdout) == normalize(q["expected"]):
                    passed += 1
                    print(f"  OK   {q['id']}")
                else:
                    failed += 1
                    problems.append(f"{q['id']}: 输出不符。预期 {q['expected']!r}，实际 {normalize(stdout)!r}")
        shutil.rmtree(VERIFY_DIR, ignore_errors=True)
        print(f"编程题参考答案：通过 {passed}，失败 {failed}")

    if problems:
        print("\n问题：")
        for p in problems:
            print("  -", p)
        sys.exit(1)
    print("检查通过。")


if __name__ == "__main__":
    main()
