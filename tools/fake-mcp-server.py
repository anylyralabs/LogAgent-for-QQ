#!/usr/bin/env python3
"""Minimal MCP stdio server for LogAgent E2E testing (JSON-RPC 2.0 over newline-delimited lines).

Tools: echo (echo back text), add (sum two numbers). No external dependencies.
"""
import json
import sys

# Force UTF-8 regardless of Windows console defaults
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def log(msg: str) -> None:
    print(f"[fake-mcp] {msg}", file=sys.stderr, flush=True)


def send(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


TOOLS = [
    {
        "name": "echo",
        "description": "回显输入文本（测试 MCP 工具调用链路用）",
        "inputSchema": {
            "type": "object",
            "properties": {"text": {"type": "string", "description": "要回显的文本"}},
            "required": ["text"],
        },
    },
    {
        "name": "add",
        "description": "计算两个数之和",
        "inputSchema": {
            "type": "object",
            "properties": {
                "a": {"type": "number", "description": "第一个数"},
                "b": {"type": "number", "description": "第二个数"},
            },
            "required": ["a", "b"],
        },
    },
]


def main() -> None:
    log("started")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception as exc:  # noqa: BLE001
            log(f"bad json: {exc}")
            continue

        method = msg.get("method")
        mid = msg.get("id")

        if method == "initialize":
            send({
                "jsonrpc": "2.0",
                "id": mid,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fake-mcp", "version": "1.0.0"},
                },
            })
        elif method == "notifications/initialized":
            log("handshake complete")
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": mid, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            params = msg.get("params") or {}
            name = params.get("name")
            args = params.get("arguments") or {}
            if name == "echo":
                text = str(args.get("text", ""))
                result = {
                    "content": [{"type": "text", "text": f"回显：{text}"}],
                    "isError": False,
                }
            elif name == "add":
                try:
                    total = float(args.get("a", 0)) + float(args.get("b", 0))
                    shown = int(total) if total.is_integer() else total
                    result = {
                        "content": [{"type": "text", "text": f"计算结果：{shown}"}],
                        "isError": False,
                    }
                except Exception as exc:  # noqa: BLE001
                    result = {
                        "content": [{"type": "text", "text": f"参数错误：{exc}"}],
                        "isError": True,
                    }
            else:
                result = {
                    "content": [{"type": "text", "text": f"未知工具：{name}"}],
                    "isError": True,
                }
            send({"jsonrpc": "2.0", "id": mid, "result": result})
        else:
            if mid is not None:
                send({
                    "jsonrpc": "2.0",
                    "id": mid,
                    "error": {"code": -32601, "message": f"method not found: {method}"},
                })


if __name__ == "__main__":
    main()
