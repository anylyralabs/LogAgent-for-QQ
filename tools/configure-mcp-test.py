"""Configure the fake MCP server into LogAgent config for E2E testing."""
import json
import os

p = os.path.join(os.environ['APPDATA'], 'LogAgent', 'config.json')
with open(p, encoding='utf-8') as f:
    cfg = json.load(f)

cfg.setdefault('Mcp', {})
cfg['Mcp']['Enabled'] = True
cfg['Mcp']['Servers'] = [{
    'Id': 'fakemcp',
    'Name': '演示 MCP 服务器',
    'Transport': 'stdio',
    'Command': 'python',
    'Args': 'E:/logagent-for-qq/tools/fake-mcp-server.py',
    'WorkingDirectory': '',
    'Env': {},
    'Url': '',
    'Headers': {},
    'Enabled': True,
}]

with open(p, 'w', encoding='utf-8') as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)

print('Mcp.Servers:', json.dumps(cfg['Mcp']['Servers'], ensure_ascii=False)[:200])
print('Providers count:', len(cfg.get('Providers', [])))
print('Known skills disabled:', cfg.get('Skills', {}).get('DisabledSkills', []))
