"""E2E: stage the real 29.6MB latest_game.log from the QQ group into LogAgent's files dir.

Mimics exactly what MessageEnricher does when a QQ file attachment arrives:
ask NapCat for the file list -> resolve download URL -> stream to files dir.
"""
import json
import os
import urllib.request
import datetime

NAPcat = "http://127.0.0.1:3000"
GROUP = int(os.environ.get("LOGAGENT_TEST_GROUP", "0"))  # 测试目标群号（自行设置环境变量）
FILES_DIR = os.path.join(os.environ["APPDATA"], "LogAgent", "files")


def api(action, payload):
    req = urllib.request.Request(
        f"{NAPcat}/{action}",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


# 1) find the file in group root
data = api("get_group_root_files", {"group_id": GROUP}).get("data", {})
files = data.get("files", [])
target = None
for f in files:
    print("file:", f.get("file_name"), f.get("file_id"), f.get("file_size"))
    if "latest_game" in (f.get("file_name") or ""):
        target = f
if not target:
    # try folders
    for folder in data.get("folders", []):
        sub = api("get_group_files_by_folder", {"group_id": GROUP, "folder_id": folder.get("folder_id")}).get("data", {})
        for f in sub.get("files", []):
            print("sub file:", f.get("file_name"), f.get("file_id"), f.get("file_size"))
            if "latest_game" in (f.get("file_name") or ""):
                target = f
if not target:
    print("TARGET_NOT_FOUND")
    raise SystemExit(1)

print("target:", target.get("file_name"), "size:", target.get("file_size"))

# 2) resolve download url
url_resp = api(
    "get_group_file_url",
    {"group_id": GROUP, "file_id": target["file_id"], "busid": target.get("busid", 0)},
)
url = url_resp.get("data", {}).get("url", "")
print("url head:", url[:80])
if not url:
    print("URL_FAILED:", json.dumps(url_resp, ensure_ascii=False)[:300])
    raise SystemExit(1)

# 3) stream to files dir (timestamped name like the enricher's StagedPath)
os.makedirs(FILES_DIR, exist_ok=True)
name = f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{target['file_name']}"
dest = os.path.join(FILES_DIR, name)
total = 0
with urllib.request.urlopen(url, timeout=300) as resp, open(dest, "wb") as out:
    while True:
        chunk = resp.read(65536)
        if not chunk:
            break
        out.write(chunk)
        total += len(chunk)
print("staged:", dest)
print("bytes:", total)
