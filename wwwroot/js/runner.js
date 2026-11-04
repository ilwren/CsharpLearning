// 运行器（页面侧）：管理两个 Web Worker，对 Blazor 与 Monaco 提供统一接口。
//   core  ：交互式运行（编译、运行、等待输入）。
//   tools ：批改（batch）与代码补全（complete）。
// 超时、输出上限、停止都在这里处理：超时或停止会直接 terminate Worker（同步代码无法被安全中断，只能这样做）。
// 本文件是普通脚本，先于 Blazor 加载，暴露 window.ltRunner。
window.ltRunner = (function () {
    const RUN_LIMIT_MS = 10000;   // 程序自身运行时间上限；等待输入的时间不计入
    const OUTPUT_LIMIT = 20000;   // 输出字符上限
    const INPUT_BYTES = 65536;    // 输入缓冲区大小
    const encoder = new TextEncoder();
    // 站点根目录：由本脚本自身的地址推算（js/runner.js 的上一级），与页面路径无关（章节页在 /chapter/xx 下）
    const ROOT = new URL('../', document.currentScript.src).href;

    // 一个 Worker 宿主：负责启动（共享内存 + init）、请求/响应、终止
    class Host {
        constructor(path) {
            this.path = path;
            this.worker = null;
            this.ready = null;
            this.seq = 0;
            this.waiters = new Map();
            this.ctrl = null;
            this.data = null;
        }

        start() {
            if (this.ready) return this.ready;
            const sab = new SharedArrayBuffer(8 + INPUT_BYTES);
            this.ctrl = new Int32Array(sab, 0, 2);
            this.data = new Uint8Array(sab, 8);
            const worker = new Worker(new URL(this.path, ROOT).href, { type: 'module' });
            this.worker = worker;
            worker.onmessage = (e) => this.onMessage(worker, e.data);
            worker.onerror = (e) => this.onCrash(worker, '运行器崩溃' + (e && e.message ? '：' + e.message : ''));
            const ready = this.request({ type: 'init', sab: sab, baseUrl: ROOT }, null);
            this.ready = ready;
            ready.catch(() => { if (this.worker === worker) this.reset(); });
            return ready;
        }

        request(msg, onEvent) {
            const id = ++this.seq;
            return new Promise((resolve, reject) => {
                this.waiters.set(id, { resolve: resolve, reject: reject, onEvent: onEvent });
                this.worker.postMessage(Object.assign({ id: id }, msg));
            });
        }

        onMessage(worker, m) {
            if (worker !== this.worker) return;   // 已被终止的旧 Worker 的消息，忽略
            const w = this.waiters.get(m.id);
            if (m.type === 'ev') {
                if (w && w.onEvent) w.onEvent(m.kind, m.text);
                return;
            }
            if (!w) return;
            this.waiters.delete(m.id);
            if (m.type === 'done') w.resolve(m.result);
            else w.reject(new Error(m.text || '运行器错误'));
        }

        onCrash(worker, message) {
            if (worker !== this.worker) return;
            this.failAll(message);
            this.reset();
        }

        failAll(message) {
            const list = Array.from(this.waiters.values());
            this.waiters.clear();
            list.forEach((w) => w.reject(new Error(message)));
        }

        // 终止 Worker（超时、停止、崩溃都走这里）。下次使用时会重新启动。
        terminate(reason) {
            if (this.worker) this.worker.terminate();
            this.reset();
            this.failAll(reason || '已终止');
        }

        reset() {
            this.worker = null;
            this.ready = null;
        }

        // 写入一行输入并唤醒 Worker
        writeLine(text) {
            const bytes = encoder.encode(text);
            const n = Math.min(bytes.length, this.data.length);
            this.data.set(bytes.subarray(0, n), 0);
            Atomics.store(this.ctrl, 1, n);
            Atomics.store(this.ctrl, 0, 1);
            Atomics.notify(this.ctrl, 0);
        }

        closeInput() {
            Atomics.store(this.ctrl, 1, -1);
            Atomics.store(this.ctrl, 0, 1);
            Atomics.notify(this.ctrl, 0);
        }
    }

    const core = new Host('runner/core/main.js');
    const tools = new Host('runner/tools/main.js');

    // 当前交互会话（同一时间只有一个）
    let session = null;

    function notify(s, kind, text, code) {
        try {
            s.ref.invokeMethodAsync('OnEvent', kind, text || '', code || 0).catch(function () { });
        } catch (e) { /* 引用已释放（页面已离开或会话已被丢弃） */ }
    }

    function clearTimer(s) {
        if (s.timer) { clearTimeout(s.timer); s.timer = null; }
    }

    function armTimer(s) {
        clearTimer(s);
        const left = RUN_LIMIT_MS - s.busy;
        s.segStart = performance.now();
        s.timer = setTimeout(function () { timeout(s); }, Math.max(0, left));
    }

    function pauseClock(s) {
        if (s.segStart) s.busy += performance.now() - s.segStart;
        s.segStart = 0;
        clearTimer(s);
    }

    function finish(s) {
        if (s.done) return false;
        s.done = true;
        clearTimer(s);
        if (session === s) session = null;
        return true;
    }

    function timeout(s) {
        if (!finish(s)) return;
        core.terminate('超时');
        notify(s, 'timeout');
        warm();
    }

    function stopSession(s) {
        if (!finish(s)) return;
        core.terminate('已停止');
        notify(s, 'exit', '', 137);
        warm();
    }

    // 输出超过上限：先报告，再终止
    function outputLimit(s) {
        if (!finish(s)) return;
        core.terminate('输出超限');
        notify(s, 'out', '\n[输出超过 ' + OUTPUT_LIMIT + ' 字符，已强制停止]\n');
        notify(s, 'exit', '', 137);
        warm();
    }

    function warm() {
        core.start().catch(function () { });
    }

    function onCoreEvent(s, kind, text) {
        if (s.done) return;
        switch (kind) {
            case 'build':
                s.resolveBuild(text);
                break;
            case 'out':
                s.out += text.length;
                if (s.out > OUTPUT_LIMIT) {
                    outputLimit(s);
                    return;
                }
                notify(s, 'out', text);
                break;
            case 'input':
                pauseClock(s);
                notify(s, 'input');
                break;
            case 'exit':
                if (finish(s)) {
                    notify(s, 'exit', '', parseInt(text, 10));
                    warm();
                }
                break;
            case 'error':
                notify(s, 'out', '\n[运行器错误] ' + text + '\n');
                break;
        }
    }

    // 编译并开始运行。返回 { built, text }：text 为编译结果（第一行 OK/FAIL，之后为诊断）。
    // 编译成功后程序在后台运行，事件通过 ref.invokeMethodAsync('OnEvent', kind, text, code) 发送。
    async function start(code, ref) {
        if (session) stopSession(session);
        await core.start();

        const s = { ref: ref, out: 0, busy: 0, segStart: 0, timer: null, done: false };
        let resolveBuild;
        const build = new Promise(function (r) { resolveBuild = r; });
        s.resolveBuild = resolveBuild;
        session = s;

        const running = core.request({ type: 'run', code: code, batch: false }, function (kind, text) {
            onCoreEvent(s, kind, text);
        });
        running.then(function () {
            // 程序结束的 exit 事件已经先到达；若没有（例如编译失败），这里只做清理
            if (session === s && !s.done && s.resolvedOk !== true) {
                finish(s);
            }
        }, function (err) {
            if (finish(s)) {
                notify(s, 'out', '\n[运行器错误] ' + err.message + '\n');
                notify(s, 'exit', '', 134);
            }
            resolveBuild('FAIL\n运行器已停止');
        });

        const text = await build;
        if (text.split('\n')[0] !== 'OK') {
            finish(s);
            return { built: false, text: text };
        }
        s.resolvedOk = true;
        armTimer(s);
        return { built: true, text: text };
    }

    function sendInput(text) {
        const s = session;
        if (!s || s.done) return;
        core.writeLine(text);
        armTimer(s);
    }

    function closeInput() {
        const s = session;
        if (!s || s.done) return;
        core.closeInput();
        armTimer(s);
    }

    function stop() {
        const s = session;
        if (s) stopSession(s);
    }

    // 批改：不交互（读取输入得到 EOF），返回输出与退出码。
    async function batch(code) {
        await tools.start();
        let buildText = '';
        let output = '';
        let exitCode = null;
        let timedOut = false;
        let timer = null;
        try {
            const runP = tools.request({ type: 'run', code: code, batch: true }, function (kind, text) {
                if (kind === 'build') buildText = text;
                else if (kind === 'out') output += text;
                else if (kind === 'exit') exitCode = parseInt(text, 10);
            });
            runP.catch(function () { });   // 超时时 Worker 被终止，这里的拒绝由下面的 race 处理
            const limit = new Promise(function (_, reject) {
                timer = setTimeout(function () {
                    timedOut = true;
                    tools.terminate('超时');
                    reject(new Error('超时'));
                }, RUN_LIMIT_MS);
            });
            await Promise.race([runP, limit]);
        } catch (e) {
            if (!timedOut) output += '\n[运行器错误] ' + e.message + '\n';
        } finally {
            clearTimeout(timer);
        }
        const built = buildText.split('\n')[0] === 'OK';
        return { built: built, buildText: buildText, output: output, exitCode: exitCode, timedOut: timedOut };
    }

    // 代码补全：返回文本，每行 label \t kind \t detail \t rank
    async function complete(code, offset) {
        try {
            await tools.start();
            return await tools.request({ type: 'complete', code: code, offset: offset }, null);
        } catch (e) {
            return '';
        }
    }

    return {
        start: start,
        sendInput: sendInput,
        closeInput: closeInput,
        stop: stop,
        batch: batch,
        complete: complete,
        warm: warm
    };
})();
