# 本地 Mock OpenAI 兼容服务（端到端测试用，不连外网）：
#   GET  /v1/models             -> 返回模型列表
#   POST /v1/chat/completions   -> 返回固定补全
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODELS = ["deepseek-chat", "deepseek-reasoner", "deepseek-embedding", "bge-large-zh"]


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.rstrip("/").endswith("/models"):
            body = {"object": "list", "data": [{"id": m, "object": "model"} for m in MODELS]}
            self._send(200, body)
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path.rstrip("/").endswith("/chat/completions"):
            length = int(self.headers.get("Content-Length") or 0)
            self.rfile.read(length)
            body = {
                "choices": [{"message": {"role": "assistant", "content": "正常"}}],
                "usage": {"prompt_tokens": 8, "completion_tokens": 2},
            }
            self._send(200, body)
        else:
            self._send(404, {"error": "not found"})

    def _send(self, code, obj):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 18899), Handler).serve_forever()
