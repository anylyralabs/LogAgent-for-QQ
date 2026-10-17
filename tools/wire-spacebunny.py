"""Wire Space Bunny (via WorkBuddy proxy) into LogAgent config.

Phase A (test):   provider added, hy4 disabled, all chains = [spacebunny01]
Phase B (final):  hy4 re-enabled, chains = [spacebunny01, hy4wb01]  (usage: python wire-spacebunny.py final)
"""
import json
import os
import sys

p = os.path.expandvars(r"%APPDATA%\LogAgent\config.json")
cfg = json.load(open(p, encoding="utf-8"))

phase = sys.argv[1] if len(sys.argv) > 1 else "test"

hy4 = next(x for x in cfg["Providers"] if x["Id"] == "hy4wb01")
bunny = next((x for x in cfg["Providers"] if x["Id"] == "spacebunny01"), None)
if bunny is None:
    bunny = {
        "Id": "spacebunny01",
        "Name": "Space Bunny（免费）",
        "Endpoint": hy4["Endpoint"],      # 同一 WorkBuddy 反代
        "ApiKey": hy4["ApiKey"],
        "ChatModel": "space-bunny",
        "MaxTokens": 64000,
        "ContextTokens": 1000000,
        "ReasoningModel": True,
        "Temperature": 0.7,
        "TimeoutSeconds": 120,
        "Enabled": True,
    }
    cfg["Providers"].append(bunny)
    print("provider added")
bunny["Enabled"] = True

hy4["Enabled"] = phase != "test"   # 测试期禁用 hy4，确保回复一定来自 space-bunny
for rule in cfg["Rules"]:
    if rule["TaskType"] == "Embedding":
        continue
    rule["ProviderChain"] = ["spacebunny01"] + (["hy4wb01"] if phase != "test" else [])

json.dump(cfg, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print(f"phase={phase} | chains:", {r["TaskType"]: r["ProviderChain"] for r in cfg["Rules"]})
print("hy4.Enabled:", hy4["Enabled"], "| bunny.Enabled:", bunny["Enabled"])
