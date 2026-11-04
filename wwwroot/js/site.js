// 站点前端脚本：Monaco 编辑器（语法高亮、补全、错误标记）与章节页的代码块、平台分页、自测勾选框。
// 所有函数挂在 window.lt 上，由 Blazor 通过 JS 互操作调用。
(function () {
    const MONACO_BASE = '/lib/monaco/vs';
    const THEME_KEY = 'lt-dark';
    const PLATFORM_KEY = 'lt-platform';

    const editors = new Map();   // id -> Promise<{ monaco, editor, model }>
    let monacoPromise = null;
    let completionRegistered = false;

    // ---------- 安全存储 ----------
    // 站点嵌在别的网站的 iframe 里时，浏览器可能禁止访问 localStorage，直接读取会抛异常。
    // 异常时退回内存存储：偏好和勾选状态只在本次打开期间有效，但脚本照常运行。
    const store = (function () {
        try {
            const s = window.localStorage;
            const probe = '__lt_probe__';
            s.setItem(probe, '1');
            s.removeItem(probe);
            return s;
        } catch (e) {
            const mem = new Map();
            return {
                getItem: function (k) { return mem.has(k) ? mem.get(k) : null; },
                setItem: function (k, v) { mem.set(k, String(v)); },
                removeItem: function (k) { mem.delete(k); }
            };
        }
    })();
    let dark = store.getItem(THEME_KEY) === '1';

    // ---------- 加载 Monaco ----------
    function loadMonaco() {
        if (monacoPromise) return monacoPromise;
        monacoPromise = new Promise((resolve, reject) => {
            window.MonacoEnvironment = {
                getWorkerUrl: function () { return MONACO_BASE + '/editor/editor.worker.js'; }
            };
            const script = document.createElement('script');
            script.src = MONACO_BASE + '/loader.js';
            script.onload = function () {
                window.require.config({ paths: { vs: MONACO_BASE } });
                window.require(['vs/editor/editor.main'], function () {
                    resolve(window.monaco);
                }, reject);
            };
            script.onerror = function () { reject(new Error('Monaco 编辑器加载失败')); };
            document.head.appendChild(script);
        });
        return monacoPromise;
    }

    // ---------- 代码补全（浏览器内 Roslyn，运行在 tools Worker 中） ----------
    function registerCompletion(monaco) {
        if (completionRegistered) return;
        completionRegistered = true;
        const K = monaco.languages.CompletionItemKind;
        const kindMap = {
            method: K.Method, class: K.Class, struct: K.Struct, interface: K.Interface,
            enum: K.Enum, enummember: K.EnumMember, field: K.Field, property: K.Property,
            event: K.Event, delegate: K.Function, namespace: K.Module, module: K.Module,
            keyword: K.Keyword, snippet: K.Snippet, variable: K.Variable, constant: K.Constant,
            typeparameter: K.TypeParameter, text: K.Text
        };
        monaco.languages.registerCompletionItemProvider('csharp', {
            triggerCharacters: ['.'],
            provideCompletionItems: async function (model, position) {
                const word = model.getWordUntilPosition(position);
                const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
                let text = '';
                try {
                    text = await window.ltRunner.complete(model.getValue(), model.getOffsetAt(position));
                } catch (e) {
                    return { suggestions: [] };
                }
                // 每行：label \t kind \t detail \t rank
                const suggestions = text.split('\n').filter(Boolean).map(function (line) {
                    const p = line.split('\t');
                    return {
                        label: p[0],
                        kind: kindMap[p[1]] ?? K.Text,
                        insertText: p[0],
                        sortText: String(p[3] || 0).padStart(6, '0'),
                        detail: p[2] || undefined,
                        range: range
                    };
                });
                return { suggestions: suggestions };
            }
        });
    }

    // ---------- 编辑器实例 ----------
    function createEditor(id, value) {
        const promise = (async function () {
            const monaco = await loadMonaco();
            registerCompletion(monaco);
            const host = document.getElementById(id);
            const uri = monaco.Uri.parse('inmemory://model/' + id + '.cs');
            const old = monaco.editor.getModel(uri);
            if (old) old.dispose();
            const model = monaco.editor.createModel(value ?? '', 'csharp', uri);
            const editor = monaco.editor.create(host, {
                model: model,
                theme: dark ? 'vs-dark' : 'vs',
                automaticLayout: true,
                minimap: { enabled: false },
                fontSize: 14,
                tabSize: 4,
                insertSpaces: true,
                wordWrap: 'on',
                scrollBeyondLastLine: false,
                quickSuggestions: { other: true, comments: false, strings: false },
                suggestOnTriggerCharacters: true,
                fixedOverflowWidgets: true
            });
            return { monaco: monaco, editor: editor, model: model };
        })();
        editors.set(id, promise);
        return promise;
    }

    async function withEditor(id, fn) {
        const p = editors.get(id);
        if (!p) return null;
        return fn(await p);
    }

    // ---------- 代码块：语言映射 ----------
    // 左边是文档里写的围栏语言名，id 是 Monaco 的语言 id，label 是工具条上显示的名字
    const LANGS = {
        'csharp': { id: 'csharp', label: 'C#' },
        'cs': { id: 'csharp', label: 'C#' },
        'c#': { id: 'csharp', label: 'C#' },
        'bash': { id: 'shell', label: 'Bash' },
        'sh': { id: 'shell', label: 'Shell' },
        'shell': { id: 'shell', label: 'Shell' },
        'zsh': { id: 'shell', label: 'Shell' },
        'console': { id: 'shell', label: '终端' },
        'powershell': { id: 'powershell', label: 'PowerShell' },
        'ps1': { id: 'powershell', label: 'PowerShell' },
        'pwsh': { id: 'powershell', label: 'PowerShell' },
        'bat': { id: 'bat', label: 'cmd' },
        'cmd': { id: 'bat', label: 'cmd' },
        'batch': { id: 'bat', label: 'cmd' },
        'xml': { id: 'xml', label: 'XML' },
        'xaml': { id: 'xml', label: 'XAML' },
        'axaml': { id: 'xml', label: 'AXAML' },
        'csproj': { id: 'xml', label: 'csproj' },
        'html': { id: 'html', label: 'HTML' },
        'json': { id: 'json', label: 'JSON' },
        'yaml': { id: 'yaml', label: 'YAML' },
        'yml': { id: 'yaml', label: 'YAML' },
        'ini': { id: 'ini', label: 'INI' },
        'toml': { id: 'ini', label: 'TOML' },
        'gitignore': { id: 'plaintext', label: '.gitignore' },
        'fortran': { id: 'fortran', label: 'Fortran' },
        'text': { id: 'plaintext', label: '文本' },
        'txt': { id: 'plaintext', label: '文本' },
        'plaintext': { id: 'plaintext', label: '文本' },
        'output': { id: 'plaintext', label: '输出' }
    };

    function langInfo(codeEl) {
        const cls = Array.from(codeEl.classList).find(function (c) { return c.startsWith('language-'); });
        const name = cls ? cls.slice('language-'.length).toLowerCase() : '';
        return LANGS[name] || { id: 'plaintext', label: name || '文本' };
    }

    // ---------- 复制 ----------
    async function copyText(text, btn) {
        try {
            await navigator.clipboard.writeText(text);
        } catch (e) {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            ta.remove();
        }
        const old = btn.textContent;
        btn.textContent = '已复制';
        btn.classList.add('ok');
        setTimeout(function () { btn.textContent = old; btn.classList.remove('ok'); }, 1500);
    }

    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text !== undefined) e.textContent = text;
        return e;
    }

    // ---------- 平台分页（由 Blazor 渲染，这里只负责记住选择、判断本机系统） ----------
    // 分页内容在原文中用标记写定，由 ArticleSplitter 切分，PlatformTabs 组件渲染。
    function defaultPlatform() {
        const p = navigator.platform || navigator.userAgent || '';
        if (/Win/i.test(p)) return 'Windows';
        if (/Mac/i.test(p)) return 'macOS';
        return 'Linux';
    }

    // ---------- 自测清单：勾选框可点、状态记在浏览器里 ----------
    function wireChecklists(root, chapterKey) {
        const lists = Array.from(root.querySelectorAll('ul')).filter(function (ul) {
            return ul.querySelector(':scope > li > input[type="checkbox"]');
        });
        lists.forEach(function (ul, listIndex) {
            if (ul.dataset.ltWired) return;
            ul.dataset.ltWired = '1';
            const boxes = Array.from(ul.querySelectorAll(':scope > li > input[type="checkbox"]'));
            const keyOf = function (i) { return 'lt-chk:' + chapterKey + ':' + listIndex + ':' + i; };

            const progress = el('div', 'lt-progress');
            ul.parentNode.insertBefore(progress, ul);
            const refresh = function () {
                const done = boxes.filter(function (b) { return b.checked; }).length;
                progress.innerHTML = '';
                progress.appendChild(el('span', 'lt-progress-text', '已完成 ' + done + ' / ' + boxes.length));
                const bar = el('div', 'lt-progress-bar');
                const fill = el('div', 'lt-progress-fill');
                fill.style.width = (boxes.length ? (done / boxes.length * 100) : 0) + '%';
                bar.appendChild(fill);
                progress.appendChild(bar);
                progress.classList.toggle('complete', done === boxes.length && boxes.length > 0);
            };

            boxes.forEach(function (box, i) {
                box.disabled = false;
                box.classList.add('lt-check');
                box.checked = store.getItem(keyOf(i)) === '1';
                box.addEventListener('change', function () {
                    store.setItem(keyOf(i), box.checked ? '1' : '0');
                    refresh();
                });
                // 点文字也能勾选（链接除外）
                const li = box.parentElement;
                li.classList.add('lt-check-item');
                li.addEventListener('click', function (e) {
                    if (e.target.closest('a') || e.target === box) return;
                    box.click();
                });
            });
            refresh();
        });
    }

    // ---------- 代码块：高亮、工具条、复制、在编辑器中打开 ----------
    // 出错时只记录日志，不影响其他步骤
    function attempt(name, fn) {
        try {
            const r = fn();
            if (r && typeof r.then === 'function') return r.catch(function (e) { console.warn(name + ' 失败', e); });
        } catch (e) {
            console.warn(name + ' 失败', e);
        }
        return Promise.resolve();
    }

    async function decorateCode(root, monaco, ref) {
        const known = monaco ? new Set(monaco.languages.getLanguages().map(function (l) { return l.id; })) : new Set();
        for (const pre of root.querySelectorAll('pre')) {
            if (pre.dataset.ltDone) continue;
            pre.dataset.ltDone = '1';
            const code = pre.querySelector('code');
            if (!code) continue;
            const text = code.textContent;
            const info = langInfo(code);
            if (info.id !== 'plaintext' && known.has(info.id)) {
                try {
                    code.innerHTML = await monaco.editor.colorize(text, info.id, { tabSize: 4 });
                } catch (e) { /* 高亮失败时保留原文 */ }
            }
            code.classList.add('lt-code');

            const block = el('div', 'lt-codeblock');
            const bar = el('div', 'lt-codebar');
            bar.appendChild(el('span', 'lt-codebar-lang', info.label));
            const actions = el('div', 'lt-codebar-actions');
            if (info.id === 'csharp' && ref) {
                const open = el('button', 'lt-codebar-btn', '在编辑器中打开');
                open.type = 'button';
                open.addEventListener('click', function () { ref.invokeMethodAsync('OpenBlock', text); });
                actions.appendChild(open);
            }
            const copy = el('button', 'lt-codebar-btn', '复制');
            copy.type = 'button';
            copy.addEventListener('click', function () { copyText(text, copy); });
            actions.appendChild(copy);
            bar.appendChild(actions);

            pre.parentNode.insertBefore(block, pre);
            block.appendChild(bar);
            block.appendChild(pre);
        }
    }

    function scrollToHash() {
        const id = decodeURIComponent(location.hash.slice(1));
        if (!id) return;
        const target = document.getElementById(id);
        if (target) setTimeout(function () { target.scrollIntoView({ block: 'start' }); }, 50);
    }

    // ---------- 页内锚点链接 ----------
    // Blazor 会把 href="#xx" 当作路由导航（结果跳到首页），所以在捕获阶段截获，自己滚动到目标。
    function installHashLinks() {
        window.addEventListener('click', function (e) {
            if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            const a = e.target && e.target.closest ? e.target.closest('a[href^="#"]') : null;
            if (!a || a.target === '_blank') return;
            const id = decodeURIComponent(a.getAttribute('href').slice(1));
            const target = id ? document.getElementById(id) : null;
            if (!target) return;
            e.preventDefault();
            e.stopPropagation();
            target.scrollIntoView({ behavior: 'smooth', block: 'start' });
            history.replaceState(null, '', location.pathname + location.search + '#' + encodeURIComponent(id));
        }, true);
    }
    installHashLinks();

    // ---------- 窗口 API ----------
    window.lt = {
        createEditor: function (id, value) {
            return createEditor(id, value);
        },
        getPlatform: function () {
            return store.getItem(PLATFORM_KEY) || defaultPlatform();
        },
        setPlatform: function (platform) {
            store.setItem(PLATFORM_KEY, platform);
        },
        getValue: function (id) {
            return withEditor(id, function (e) { return e.model.getValue(); }).then(function (v) { return v ?? ''; });
        },
        copyEditor: function (id, btn) {
            return withEditor(id, function (e) { return e.model.getValue(); })
                .then(function (v) { return copyText(v ?? '', btn); });
        },
        focusEl: function (el) {
            if (el && el.focus) el.focus();
        },
        setValue: function (id, value) {
            return withEditor(id, function (e) { e.model.setValue(value ?? ''); });
        },
        setMarkers: function (id, markers) {
            return withEditor(id, function (e) {
                const monaco = e.monaco, model = e.model;
                const list = (markers || []).map(function (m) {
                    const word = model.getWordAtPosition({ lineNumber: m.line, column: m.column });
                    return {
                        startLineNumber: m.line,
                        startColumn: m.column,
                        endLineNumber: m.line,
                        endColumn: word ? word.endColumn : m.column + 1,
                        message: m.message,
                        severity: m.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning
                    };
                });
                monaco.editor.setModelMarkers(model, 'csharp', list);
            });
        },
        disposeEditor: async function (id) {
            const p = editors.get(id);
            editors.delete(id);
            if (!p) return;
            const e = await p;
            e.editor.dispose();
            e.model.dispose();
        },

        // ---------- 主题 ----------
        setTheme: async function (isDark) {
            dark = !!isDark;
            store.setItem(THEME_KEY, dark ? '1' : '0');
            document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
            if (monacoPromise) {
                const monaco = await monacoPromise;
                monaco.editor.setTheme(dark ? 'vs-dark' : 'vs');
            }
        },
        getStoredTheme: function () {
            return store.getItem(THEME_KEY) === '1';
        },

        // ---------- 章节页 ----------
        // 渲染后的章节正文：分页、代码块、自测勾选框、锚点跳转。重复调用是安全的。
        decorate: async function (rootId, ref, chapterKey) {
            const root = document.getElementById(rootId);
            if (!root) return;
            // 各步互不影响：某一步出错只记录日志，其余步骤照常完成。
            // 复制按钮、分页、勾选框不依赖 Monaco；语法高亮需要 Monaco，加载失败时保留原样文本。
            attempt('自测勾选框', function () { wireChecklists(root, chapterKey); });
            let monaco = null;
            try { monaco = await loadMonaco(); } catch (e) { console.warn('Monaco 未加载，代码块不做语法高亮', e); }
            await attempt('代码块', function () { return decorateCode(root, monaco, ref); });
            scrollToHash();
        },
        scrollIntoView: function (id) {
            const el = document.getElementById(id);
            if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    };

    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
})();
