// Web Worker 入口（core 与 tools 共用）：在 Worker 中启动独立的 .NET 运行时。
// 页面通过 postMessage 发送请求，请求带 id；Worker 回复 done / error，并在运行过程中发送 ev（事件）。
// 输入是阻塞的：readLine 用 Atomics.wait 等待页面写入共享内存（SharedArrayBuffer）。
import { dotnet } from './_framework/dotnet.js';

let ctrl = null;      // Int32Array：[0] 状态（0 = 等待，1 = 已提供），[1] 输入字节数（-1 = 输入结束）
let data = null;      // Uint8Array：输入内容（UTF-8）
let exp = null;       // 程序集导出的方法
let currentId = null; // 当前请求的 id，事件带上它，页面据此归属

globalThis.lt = {
    emit(kind, payload) {
        self.postMessage({ type: 'ev', id: currentId, kind: kind, text: payload });
    },
    readLine() {
        Atomics.store(ctrl, 0, 0);
        self.postMessage({ type: 'ev', id: currentId, kind: 'input', text: '' });
        Atomics.wait(ctrl, 0, 0);
        const len = Atomics.load(ctrl, 1);
        if (len < 0) return '\u0004';
        return new TextDecoder().decode(data.slice(0, len));
    }
};

self.onmessage = async (e) => {
    const m = e.data;
    currentId = m.id;
    try {
        if (m.type === 'init') {
            ctrl = new Int32Array(m.sab, 0, 2);
            data = new Uint8Array(m.sab, 8);
            const runtime = await dotnet.create();
            const cfg = runtime.getConfig();
            exp = await runtime.getAssemblyExports(cfg.mainAssemblyName);
            const count = await exp.Runner.Init(m.baseUrl);
            self.postMessage({ type: 'done', id: m.id, result: String(count) });
        } else if (m.type === 'run') {
            exp.Runner.Run(m.code, !!m.batch);
            self.postMessage({ type: 'done', id: m.id });
        } else if (m.type === 'complete') {
            const result = await exp.Runner.Complete(m.code, m.offset);
            self.postMessage({ type: 'done', id: m.id, result: result });
        }
    } catch (err) {
        self.postMessage({ type: 'error', id: m.id, text: String((err && err.stack) || err) });
    }
};
