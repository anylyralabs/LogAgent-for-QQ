# -*- coding: utf-8 -*-
"""官方 Bot WS 链路诊断：token → gateway → WS 握手（带 Authorization 头）→ Hello → Identify → READY。"""
import asyncio
import json
import os
import urllib.request

import websockets

with open(os.path.join(os.environ["APPDATA"], "LogAgent", "config.json"), encoding="utf-8") as f:
    cfg = json.load(f)["OfficialBot"]

app_id, app_secret = cfg["AppId"], cfg["AppSecret"]

# 1) token
data = json.dumps({"appId": app_id, "clientSecret": app_secret}).encode()
req = urllib.request.Request("https://bots.qq.com/app/getAppAccessToken", data=data, method="POST")
req.add_header("Content-Type", "application/json")
tok = json.loads(urllib.request.urlopen(req, timeout=15).read())["access_token"]
print("token OK")

# 2) gateway
req = urllib.request.Request("https://api.sgroup.qq.com/gateway")
req.add_header("Authorization", "QQBot " + tok)
gw = json.loads(urllib.request.urlopen(req, timeout=15).read())["url"]
print("gateway:", gw)


async def main():
    headers = {"Authorization": f"QQBot {tok}", "X-Union-Appid": app_id}
    try:
        async with websockets.connect(gw, additional_headers=headers, open_timeout=15) as ws:
            hello = json.loads(await asyncio.wait_for(ws.recv(), timeout=10))
            print("Hello op:", hello.get("op"), "heartbeat:", hello.get("d", {}).get("heartbeat_interval"))
            identify = {
                "op": 2,
                "d": {
                    "token": f"QQBot {tok}",
                    "intents": 1 << 25,
                    "shard": [0, 1],
                    "properties": {"$os": "windows", "$browser": "LogAgent", "$device": "LogAgent"},
                },
            }
            await ws.send(json.dumps(identify))
            for _ in range(2):
                msg = json.loads(await asyncio.wait_for(ws.recv(), timeout=10))
                print("recv op:", msg.get("op"), "t:", msg.get("t"), str(msg)[:150])
                if msg.get("t") in ("READY", "RESUMED"):
                    print(">>> WS 鉴权成功！")
                    return
    except Exception as e:
        print("WS ERROR:", type(e).__name__, str(e)[:300])


asyncio.run(main())
