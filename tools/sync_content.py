#!/usr/bin/env python3
"""把 tutorial/ 下的原始 Markdown 同步成站点使用的副本。

原始文档只读，不做任何修改。站点副本做以下调整：
  1. 章节链接 NN-标题.md  -> /chapter/NN（附录 -> /appendix/X）
  2. README.md            -> /
  3. 图片 images/xxx      -> /content/images/xxx（图片同时复制到 wwwroot）
  4. 删除 “**下一章 → [...](...)**” 导航行（站点自带上一章/下一章按钮）
  5. 去掉文件名里的章节标题后缀，只保留编号做 URL
  6. 指向仓库其它目录（如 ../docs/、code/）的链接改成行内代码文字
  7. 白名单域名（微软、NuGet、JetBrains、Homebrew 等）自动变成可点击链接
  8. “第 N 章”“见 N.M 节”变成跳转链接；每个 N.M 节标题前加锚点
  9. 图片后紧跟的斜体说明改成 <figure> 下的注释
 10. 代码围栏内的内容一律不改动
 11. 只收录第 0 至 MAX_CHAPTER 章（当前为第 15 章）；附录与之后的章节尚未写完，不同步

用法：python3 tools/sync_content.py
"""
import html
import json
import re
import shutil
from pathlib import Path

SITE = Path(__file__).resolve().parent.parent
SRC = SITE.parent / "tutorial"
OUT = SITE / "wwwroot" / "content" / "chapters"
QUIZ_DIR = SITE / "wwwroot" / "content" / "quizzes"
INDEX = SITE / "wwwroot" / "content" / "index.json"
IMG_SRC = SRC / "images"
IMG_OUT = SITE / "wwwroot" / "content" / "images"

# 站点只收录已经写完的章节：第 0 至 MAX_CHAPTER 章（题库也只到这一章）。
# 第 16 章之后与附录 A–D 尚未写完，暂不发布；全部写完、审阅后再调高此值，并补充题库。
MAX_CHAPTER = 15

CHAPTER_RE = re.compile(r"^(\d{2})-.+\.md$")
APPENDIX_RE = re.compile(r"^附录([A-D])-.+\.md$")

LINK_CHAPTER = re.compile(r"\]\((\d{2})-[^)]*?\.md(#[^)]*)?\)")
LINK_APPENDIX = re.compile(r"\]\(附录([A-D])-[^)]*?\.md(#[^)]*)?\)")
LINK_README = re.compile(r"\]\(README\.md(#[^)]*)?\)")
LINK_IMAGE = re.compile(r"\]\(images/([^)]+)\)")
IMG_REF = re.compile(r"/content/images/([^)\s\"']+)")
REPO_LINK = re.compile(r"\[([^\]]+)\]\((?:\.\./|code/)[^)]*\)")
NEXT_CHAPTER_LINE = re.compile(r"^\*\*下一章 → \[.*\]\(.*\)\*\*\s*$")

FENCE = re.compile(r"^\s*```")
SECTION_HEAD = re.compile(r"^(#{2,4}) (\d{1,2})\.(\d{1,2}) ")
STANDALONE_IMAGE = re.compile(r"^!\[([^\]\n]*)\]\((/content/images/[^)\s]+)\)[ \t]*$", re.MULTILINE)
FIGURE = re.compile(r"!\[([^\]\n]*)\]\(([^)\s]+)\)\n\n\*([^*\n]+)\*[ \t]*(?=\n)")

# 只把这些域名（含子域名）的写法变成链接，避免把 System.Net 之类误判成网址
DOMAIN = re.compile(
    r"(?<![A-Za-z0-9_.-])"
    r"((?:https?://)?(?:[a-z0-9-]+\.)*"
    r"(?:microsoft\.com|visualstudio\.com|nuget\.org|jetbrains\.com|brew\.sh|dot\.net|github\.com)"
    r"(?:/[A-Za-z0-9._~:/?#@!$&'*+,;=%-]*[A-Za-z0-9_/-])?)"
)
CHAPTER_REF = re.compile(r"第 (\d{1,2}) 章")
SECTION_REF = re.compile(r"(见|参见|看|参考|第) (\d{1,2})\.(\d{1,2}) 节")

# 行内可能含有链接或代码的片段：这些片段内部不做自动转换
TOKEN = re.compile(r"(`[^`\n]+`|\[[^\]\n]*\]\([^)\n]*\)|<[^>\n]+>)")

# 第 0 章图片的来源页面（图片与 Commons 页面的对应关系需人工复核）
IMAGE_SOURCES = {
    "ch00-babbage-drawing.png": (
        "https://commons.wikimedia.org/wiki/File:Babbage_difference_engine_drawing.gif",
        "Wikimedia Commons",
    ),
}


def split_fences(lines):
    """逐行返回 (行文本, 是否在代码围栏内)。围栏行本身算作围栏内。"""
    in_fence = False
    for line in lines:
        if FENCE.match(line):
            in_fence = not in_fence
            yield line, True
        else:
            yield line, in_fence


def section_set(text):
    found = set()
    for line, in_fence in split_fences(text.split("\n")):
        if in_fence:
            continue
        m = SECTION_HEAD.match(line)
        if m:
            found.add(f"{m.group(2)}.{m.group(3)}")
    return found


def href_for(text):
    return text if text.startswith("http") else "https://" + text


def transform_text(part, key, chapter_keys, sections):
    # 1. 白名单域名 -> 链接
    part = DOMAIN.sub(lambda m: f"[{m.group(1)}]({href_for(m.group(1))})", part)

    # 2. 第 N 章 -> 章节链接（同一章内不加链接）
    def chapter_link(m):
        n = int(m.group(1))
        target = f"{n:02d}"
        if target == key or target not in chapter_keys:
            return m.group(0)
        return f"[第 {n} 章](/chapter/{target})"

    part = CHAPTER_REF.sub(chapter_link, part)

    # 3. 见 N.M 节 -> 节锚点（同章用 #，跨章用 /chapter/NN#）
    def section_link(m):
        prefix, ch, sec = m.group(1), m.group(2), m.group(3)
        target = f"{int(ch):02d}"
        secid = f"{int(ch)}.{int(sec)}"
        anchor = f"s{int(ch)}-{int(sec)}"
        if target == key and secid in sections.get(key, set()):
            return f"{prefix} [{ch}.{sec} 节](#{anchor})"
        if target in chapter_keys and secid in sections.get(target, set()):
            return f"{prefix} [{ch}.{sec} 节](/chapter/{target}#{anchor})"
        return m.group(0)

    part = SECTION_REF.sub(section_link, part)
    return part


def transform_inline(line, key, chapter_keys, sections):
    parts = TOKEN.split(line)
    out = []
    for i, part in enumerate(parts):
        if i % 2 == 1:
            # 行内代码若整体就是白名单网址，做成可点击的代码链接
            if part.startswith("`") and DOMAIN.fullmatch(part[1:-1]):
                inner = part[1:-1]
                out.append(f"[`{inner}`]({href_for(inner)})")
            else:
                out.append(part)
        else:
            out.append(transform_text(part, key, chapter_keys, sections))
    return "".join(out)


def image_markup(alt: str, src: str) -> str:
    """自绘 SVG 有深色版（同名加 -dark）时输出两个 img，由 CSS 按 html[data-theme] 切换显示；其余图片保持 Markdown。"""
    name = src.rsplit("/", 1)[-1]
    if name.endswith(".svg") and (IMG_SRC / (name[:-4] + "-dark.svg")).exists():
        dark_src = src[:-4] + "-dark.svg"
        alt_attr = html.escape(alt, quote=True)
        return (
            f'<img class="lt-img-light" src="{src}" alt="{alt_attr}" />\n'
            f'<img class="lt-img-dark" src="{dark_src}" alt="{alt_attr}" />'
        )
    return f"![{alt}]({src})"


def figure(m):
    alt, src, caption = m.group(1), m.group(2), m.group(3)
    name = src.rsplit("/", 1)[-1]
    cap = html.escape(caption, quote=False)
    if name in IMAGE_SOURCES:
        url, label = IMAGE_SOURCES[name]
        cap = cap.replace(label, f'<a href="{url}" target="_blank" rel="noopener">{label}</a>')
    img = image_markup(alt, src)
    return (
        '<figure class="lt-figure">\n\n'
        f"{img}\n\n"
        f'<figcaption class="lt-note"><span class="lt-note-tag">注</span>{cap}</figcaption>\n\n'
        "</figure>"
    )


def convert(text: str, key: str, chapter_keys: set, sections: dict) -> str:
    lines = [ln for ln in text.splitlines() if not NEXT_CHAPTER_LINE.match(ln)]
    text = "\n".join(lines) + "\n"
    text = LINK_CHAPTER.sub(lambda m: f"](/chapter/{m.group(1)}{m.group(2) or ''})", text)
    text = LINK_APPENDIX.sub(lambda m: f"](/appendix/{m.group(1)}{m.group(2) or ''})", text)
    text = LINK_README.sub(lambda m: f"](/{m.group(1) or ''})", text)
    text = LINK_IMAGE.sub(lambda m: f"](/content/images/{m.group(1)})", text)
    text = REPO_LINK.sub(lambda m: "`" + m.group(1).strip("`") + "`", text)
    text = FIGURE.sub(figure, text)
    text = STANDALONE_IMAGE.sub(lambda m: image_markup(m.group(1), m.group(2)), text)

    out = []
    for line, in_fence in split_fences(text.split("\n")):
        if in_fence:
            out.append(line)
            continue
        m = SECTION_HEAD.match(line)
        if m:
            out.append(f'<a id="s{int(m.group(2))}-{int(m.group(3))}" class="lt-anchor-target"></a>')
            out.append("")
            out.append(line)
            continue
        stripped = line.lstrip()
        if line.startswith("#") or stripped.startswith("<") or not line.strip():
            out.append(line)
            continue
        out.append(transform_inline(line, key, chapter_keys, sections))
    return "\n".join(out)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    sources = {}  # 站点 key -> (目标文件名, 原文)
    skipped = []  # 未收录的文件（第 16 章之后与附录），只在输出中提示
    for path in sorted(SRC.glob("*.md")):
        name = path.name
        m = CHAPTER_RE.match(name)
        a = APPENDIX_RE.match(name)
        if m and int(m.group(1)) <= MAX_CHAPTER:
            sources[m.group(1)] = (f"{m.group(1)}.md", path.read_text(encoding="utf-8"))
        elif m or a:
            skipped.append(name)

    chapter_keys = {k for k in sources if k.isdigit()}
    sections = {k: section_set(sources[k][1]) for k in chapter_keys}

    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    titles = {}
    used_images = set()
    for key, (fname, text) in sources.items():
        converted = convert(text, key, chapter_keys, sections)
        (OUT / fname).write_text(converted, encoding="utf-8")
        count += 1
        used_images.update(IMG_REF.findall(converted))
        # 标题取第一行 "# "（与站点目录一致）
        h1 = next((l for l in converted.split("\n") if l.startswith("# ")), None)
        titles[key] = h1[2:].strip().replace("`", "") if h1 else key

    # 清理上次同步遗留的文件：未收录的章节副本、不再被引用的图片
    for stale in OUT.glob("*.md"):
        if stale.stem not in sources:
            stale.unlink()

    # 目录索引：章节按 key 的字典序，题库取 quizzes 目录里实际存在的文件
    quiz_keys = sorted(p.stem for p in QUIZ_DIR.glob("*.json")) if QUIZ_DIR.exists() else []
    index = {
        "chapters": [{"key": k, "title": titles[k]} for k in sorted(titles)],
        "quizzes": quiz_keys,
    }
    INDEX.write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # 只复制被收录章节引用到的图片（深色版 -dark.svg 也由正文引用，已包含在内）
    IMG_OUT.mkdir(parents=True, exist_ok=True)
    img_count = 0
    for img in IMG_OUT.iterdir():
        if img.is_file() and img.name not in used_images:
            img.unlink()
    if IMG_SRC.exists():
        for name in sorted(used_images):
            src = IMG_SRC / name
            if src.is_file():
                shutil.copy2(src, IMG_OUT / name)
                img_count += 1
            else:
                print(f"警告：正文引用的图片不存在：{name}")

    print(f"已同步 {count} 篇文档（未收录 {len(skipped)} 篇：{'、'.join(skipped)}），"
          f"{img_count} 张图片，索引 {len(index['chapters'])} 章、题库 {len(quiz_keys)} 份 -> {OUT}")


if __name__ == "__main__":
    main()
