"""Generate the ASCII-only PowerShell test script for the file-tools E2E.

The PowerShell script must stay pure ASCII (PS 5.1 reads BOM-less UTF-8 .ps1
as ANSI and a stray multi-byte char can swallow the next line), so the Chinese
message text is embedded as char codes.
"""
real_path = r"C:\Users\Administrator\AppData\Roaming\LogAgent\files\20261015-230517-latest_game.log"

text = (
    "帮我用工具分析一份日志，本机路径：" + real_path + "\n"
    "请按这个流程：1) 用 search_file 搜索 SIGSEGV 和 Exception；"
    "2) 用 read_file 精读命中区域附近的日志行；"
    "3) 用 run_shell 跑一条 findstr 命令交叉验证。最后给出崩溃原因结论。"
)

codes = ",".join(f"0x{ord(c):X}" for c in text)
script = (
    "# One-off E2E: ask the bot to analyze a staged 29.6MB log with builtin file tools.\n"
    "# ASCII-only script on purpose (PS 5.1 reads BOM-less UTF-8 .ps1 as ANSI).\n"
    "$codes = @(" + codes + ")\n"
    "$text = -join ($codes | ForEach-Object { [char]$_ })\n"
    "& \"$PSScriptRoot\\chat-test.ps1\" -Text $text\n"
)

out = r"E:\logagent-for-qq\tools\chat-filetools-test.ps1"
with open(out, "w", encoding="utf-8") as f:
    f.write(script)

data = open(out, "rb").read()
print("ascii-only:", all(b < 128 for b in data), "| chars:", len(text))
