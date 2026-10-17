# -*- coding: utf-8 -*-
"""官方 Bot 连接诊断：token 获取 + 网关获取（正式/沙箱对比）。"""
import json
import os
import urllib.request
import urllib.error

with open(os.path.join(os.environ["APPDATA"], "LogAgent", "config.json"), encoding="utf-8") as f:
    cfg = json.load(f)["OfficialBot"]

app_id, app_secret = cfg["AppId"], cfg["AppSecret"]


def post(url, payload, headers=None):
    data = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=data, method="POST")
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:300]
    except Exception as e:
        return -1, str(e)


def get(url, headers=None):
    req = urllib.request.Request(url)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:300]
    except Exception as e:
        return -1, str(e)


status, body = post("https://bots.qq.com/app/getAppAccessToken",
                    {"appId": app_id, "clientSecret": app_secret})
print("== token ==", status)
if isinstance(body, dict) and body.get("access_token"):
    tok = body["access_token"]
    print("token OK, len:", len(tok), "expires_in:", body.get("expires_in"))
    for name, base in [("prod", "https://api.sgroup.qq.com"), ("sandbox", "https://sandbox.api.sgroup.qq.com")]:
        st, gb = get(base + "/websocket", {"Authorization": f"QQBot {tok}"})
        print(f"== gateway[{name}] ==", st, str(gb)[:220])
else:
    print("token FAIL:", str(body)[:300])
