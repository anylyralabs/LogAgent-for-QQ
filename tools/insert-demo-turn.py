# 插入一条演示问答对到 local-console（验证对话页历史回显路径）
import sqlite3, os

db = os.path.join(os.environ['APPDATA'], 'LogAgent', 'data', 'logagent.db')
conn = sqlite3.connect(db, timeout=10)

sid = 'verify-sess-local'
conn.execute(
    "INSERT OR IGNORE INTO sessions (id, chat_id, chat_type, start_time) VALUES (?, 'local-console', 'Private', '2026-10-15T14:40:00+08:00')",
    (sid,))
conn.execute(
    "INSERT INTO turns (session_id, user_input, agent_reply, quality_tag, model_used, ts) "
    "VALUES (?, ?, ?, 'none', NULL, '2026-10-15T14:40:05+08:00')",
    (sid,
     '你好呀，做个自我介绍吧',
     '你好！我是小炎，一只住在 QQ 群里的日志分析小助手。可以直接把日志丢给我分析，也可以聊技术问题——配置好模型后，我们在这个对话页里就能开聊啦。'))
conn.commit()

n = conn.execute(
    "SELECT COUNT(*) FROM turns t JOIN sessions s ON s.id=t.session_id WHERE s.chat_id='local-console'").fetchone()[0]
print('turns for local-console now:', n)
conn.close()
