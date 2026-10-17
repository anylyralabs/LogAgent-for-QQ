"""E2E: verify NapCat accepts the exact forward-card payload shape used by SendForwardAsync.
Sends a 3-node demo card (格式化演示) to the LogShare group, prints message_id, then recalls it.
"""
import json
import os
import urllib.request

NAPCAT = "http://127.0.0.1:3000"
GROUP = int(os.environ.get("LOGAGENT_TEST_GROUP", "0"))  # 测试目标群号（自行设置环境变量）


def api(action, payload):
    req = urllib.request.Request(
        f"{NAPCAT}/{action}",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


sections = [
    "📋 格式化演示（合并转发卡片）\n这是小炎新版发送格式的测试卡，稍后会自动撤回。",
    "【结论】合并转发链路正常。\n（本节对应模型输出的【结论】分节）",
    "【原因分析】\n1. 群友发的大日志会完整落盘到 files 目录；\n2. 小炎用 search_file / read_file / run_shell 自行取证。",
    "【解决方法】\n1. 等待下一条真实日志消息；\n2. 观察卡片是否按分节展示。",
]

nodes = [
    {
        "type": "node",
        "data": {
            "user_id": int(os.environ.get("LOGAGENT_TEST_BOT_QQ", "0")),  # 机器人 QQ 号（自行设置环境变量）
            "nickname": "小炎",
            "content": [{"type": "text", "data": {"text": s}}],
        },
    }
    for s in sections
]

resp = api(
    "send_group_forward_msg",
    {"group_id": GROUP, "messages": nodes},
)
print("retcode:", resp.get("retcode"), "| status:", resp.get("status"))
data = resp.get("data") or {}
msg_id = data.get("message_id") or (data.get("resId") and None)
print("message_id:", data.get("message_id"), "| resId:", data.get("resId"))

if resp.get("retcode") == 0 and data.get("message_id"):
    del_resp = api("delete_msg", {"message_id": data["message_id"]})
    print("recall retcode:", del_resp.get("retcode"))
else:
    print("FULL RESP:", json.dumps(resp, ensure_ascii=False)[:400])
