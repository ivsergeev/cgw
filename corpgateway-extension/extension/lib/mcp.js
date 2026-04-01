// CorpGateway Extension — MCP JSON-RPC handler
// 5 meta-tools: cgw_groups, cgw_list, cgw_schema, cgw_invoke, cgw_health

import {
  getEnabledGroups, getEnabledSkills, getGroups
} from './storage.js';
import { invokeSkill } from './executor.js';

// ── Handle JSON-RPC request ──────────────────────────────────

export async function handleMcpRequest(request) {
  let json;
  try {
    json = typeof request === 'string' ? JSON.parse(request) : request;
  } catch {
    return jsonRpcError(null, -32700, 'Parse error');
  }

  const method = json.method || '';
  const id = json.id ?? null;
  const params = json.params || {};

  // Notifications (no id)
  if (id === null || id === undefined) {
    return null; // 202 Accepted, no response
  }

  switch (method) {
    case 'initialize':
      return handleInitialize(id);
    case 'tools/list':
      return handleToolsList(id);
    case 'tools/call':
      return await handleToolsCall(id, params);
    case 'ping':
      return jsonRpcResult(id, {});
    default:
      return jsonRpcError(id, -32601, `Method not found: ${method}`);
  }
}

// ── initialize ──────────────────────────────────────────────

function handleInitialize(id) {
  // Note: initialize is normally handled by cgw_mcp server (which has mcpInstructions).
  // This is a fallback if called directly to the extension.
  return jsonRpcResult(id, {
    protocolVersion: '2025-03-26',
    capabilities: { tools: {} },
    serverInfo: { name: 'CorpGateway', version: '1.0.0' },
    instructions: ''
  });
}

// ── tools/list ──────────────────────────────────────────────

function handleToolsList(id) {
  const tools = [
    {
      name: 'cgw_groups',
      description: 'List available skill groups with descriptions. Call first to discover what corporate systems are connected.',
      inputSchema: { type: 'object', properties: {}, required: [] }
    },
    {
      name: 'cgw_list',
      description: 'List available skills. Returns compact text with skill signatures and descriptions. Optionally filter by group.',
      inputSchema: {
        type: 'object',
        properties: {
          group: { type: 'string', description: 'Group ID to filter by (from cgw_groups). Omit for all skills.' }
        },
        required: []
      }
    },
    {
      name: 'cgw_schema',
      description: 'Get parameter details for a specific skill. Returns name, description, and typed parameters.',
      inputSchema: {
        type: 'object',
        properties: {
          skill: { type: 'string', description: 'Skill name (from cgw_list).' }
        },
        required: ['skill']
      }
    },
    {
      name: 'cgw_invoke',
      description: 'Call a corporate skill and return the JSON response. Confirm with user before calling write operations.',
      inputSchema: {
        type: 'object',
        properties: {
          skill: { type: 'string', description: 'Skill name.' },
          params: { type: 'object', description: 'Key-value parameters. All values as strings.', additionalProperties: { type: 'string' } }
        },
        required: ['skill']
      }
    },
    {
      name: 'cgw_health',
      description: 'Check if CorpGateway extension is active.',
      inputSchema: { type: 'object', properties: {}, required: [] }
    }
  ];
  return jsonRpcResult(id, { tools });
}

// ── tools/call ──────────────────────────────────────────────

async function handleToolsCall(id, params) {
  const toolName = params.name || '';
  const args = params.arguments || {};

  try {
    let text;
    switch (toolName) {
      case 'cgw_groups': text = await callGroups(); break;
      case 'cgw_list':   text = await callList(args); break;
      case 'cgw_schema': text = await callSchema(args); break;
      case 'cgw_invoke': text = await callInvoke(args); break;
      case 'cgw_health': text = callHealth(); break;
      default: throw new Error(`Unknown tool: ${toolName}`);
    }
    return jsonRpcResult(id, {
      content: [{ type: 'text', text }],
      isError: false
    });
  } catch (err) {
    return jsonRpcResult(id, {
      content: [{ type: 'text', text: `Error: ${err.message}` }],
      isError: true
    });
  }
}

// ── Tool implementations ────────────────────────────────────

async function callGroups() {
  const groups = await getEnabledGroups();
  return JSON.stringify({
    groups: groups.map(g => ({ id: g.id, name: g.name, description: g.description }))
  });
}

async function callList(args) {
  const groupId = args.group || null;
  const groups = await getEnabledGroups();
  const allSkills = await getEnabledSkills();

  const filteredGroups = groupId
    ? groups.filter(g => g.id === groupId)
    : groups;

  let text = '# Available skills\n';
  for (const group of filteredGroups) {
    const groupSkills = allSkills.filter(s => s.groupId === group.id);
    if (groupSkills.length === 0) continue;

    text += `\n## ${group.description || group.name}\n`;
    for (const s of groupSkills) {
      text += compactSignature(s);
      if (s.description) text += `  // ${s.description}`;
      text += '\n';
    }
  }
  return text;
}

async function callSchema(args) {
  const skillName = args.skill;
  if (!skillName) throw new Error('Missing required parameter: skill');

  const skills = await getEnabledSkills();
  const skill = skills.find(s => s.name.toLowerCase() === skillName.toLowerCase());
  if (!skill) throw new Error(`Skill not found: ${skillName}`);

  return JSON.stringify({
    name: skill.name,
    description: skill.description,
    parameters: (skill.parameters || []).map(p => ({
      name: p.name,
      type: (p.type || 'String').toLowerCase(),
      required: p.required,
      description: p.description
    }))
  });
}

async function callInvoke(args) {
  const skillName = args.skill;
  if (!skillName) throw new Error('Missing required parameter: skill');
  const result = await invokeSkill(skillName, args.params || {});
  return JSON.stringify(result);
}

function callHealth() {
  return JSON.stringify({ status: 'ok', runtime: 'extension' });
}

// ── Helpers ─────────────────────────────────────────────────

function compactSignature(skill) {
  const typeMap = { String: 'str', Integer: 'int', Float: 'float', Boolean: 'bool', Date: 'date' };
  const params = (skill.parameters || []).map(p => {
    const t = typeMap[p.type] || 'str';
    return p.required ? `${p.name}:${t}` : `${p.name}:${t}?`;
  });
  return `${skill.name}(${params.join(', ')})`;
}

function jsonRpcResult(id, result) {
  return { jsonrpc: '2.0', id, result };
}

function jsonRpcError(id, code, message) {
  return { jsonrpc: '2.0', id, error: { code, message } };
}
