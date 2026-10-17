# 给 LogAgent 配置 hy4 模型通道（通过本机 WorkBuddy Proxy）
import json, os

p = os.path.join(os.environ['APPDATA'], 'LogAgent', 'config.json')
with open(p, encoding='utf-8') as f:
    cfg = json.load(f)

provider = {
    "Id": "hy4wb01",
    "Name": "混元 Hy4",
    "Endpoint": os.environ.get("LOGAGENT_PROXY_ENDPOINT", "http://127.0.0.1:19090/v1"),
    "ApiKey": os.environ.get("LOGAGENT_PROXY_API_KEY", "<在此填入代理 API Key>"),
    "ChatModel": "hy4-preview",
    "EmbeddingModel": None,
    "MaxTokens": 8192,
    "Temperature": 0.7,
    "TimeoutSeconds": 300,
    "Enabled": True,
}

cfg['Providers'] = [provider]
cfg['DefaultProviderId'] = 'hy4wb01'
for rule in cfg.get('Rules', []):
    if rule.get('TaskType') in ('Chat', 'LogAnalysis', 'DeepReasoning', 'Summary'):
        rule['ProviderChain'] = ['hy4wb01']
    # Embedding 保持空链（未配向量模型，知识库维持关键词检索）

with open(p, 'w', encoding='utf-8') as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)

print('Provider:', provider['Name'], '|', provider['Endpoint'], '|', provider['ChatModel'])
for rule in cfg.get('Rules', []):
    print(' ', rule.get('TaskType'), '->', rule.get('ProviderChain'))
print('SAVED')
