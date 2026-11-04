#!/usr/bin/env python3
"""本地预览已发布的站点（与 Cloudflare Pages 的响应头一致）。

用法：
  ./scripts/build-site.sh
  python3 scripts/serve.py            # 默认 http://localhost:8080

注意：不要用 dotnet run 或其它不带跨源隔离头的服务器预览——没有 COOP/COEP，
浏览器不允许 SharedArrayBuffer，代码运行器将无法工作。
"""
import functools
import http.server
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'build', 'publish', 'wwwroot')
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8080

# 与 Cloudflare Pages 一致：未知路径回退到 index.html（SPA）
SPA_PREFIXES = ('/_framework', '/runner', '/refs', '/content', '/lib', '/css', '/js', '/_content')


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = dict(http.server.SimpleHTTPRequestHandler.extensions_map)
    extensions_map.update({'.wasm': 'application/wasm', '.js': 'text/javascript', '.mjs': 'text/javascript'})

    def end_headers(self):
        self.send_header('Cross-Origin-Opener-Policy', 'same-origin')
        self.send_header('Cross-Origin-Embedder-Policy', 'require-corp')
        self.send_header('Cache-Control', 'no-cache')
        super().end_headers()

    def send_head(self):
        path = self.translate_path(self.path.split('?')[0])
        if not os.path.exists(path) and not self.path.startswith(SPA_PREFIXES):
            self.path = '/index.html'
        return super().send_head()


if __name__ == '__main__':
    root = os.path.abspath(ROOT)
    if not os.path.isfile(os.path.join(root, 'index.html')):
        sys.exit('找不到 build/publish/wwwroot，请先运行 ./scripts/build-site.sh')
    server = http.server.ThreadingHTTPServer(('0.0.0.0', PORT), functools.partial(Handler, directory=root))
    print(f'预览：http://localhost:{PORT}')
    server.serve_forever()
