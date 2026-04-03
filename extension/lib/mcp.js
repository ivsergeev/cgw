// CorpGateway Extension — MCP JSON-RPC handler
// meta-tools: cgw_groups, cgw_list, cgw_schema, cgw_invoke (+cgw_invoke_confirmed in native mode)

import {
  getEnabledGroups, getEnabledSkills, getSkillByName, getGroups, getConfig
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
      return await handleToolsList(id);
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

async function handleToolsList(id) {
  const config = await getConfig();
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
      description: config.confirmMode === 'otp'
        ? 'Get skill details. Returns parameters and confirm flag. Always call this before invoking a skill for the first time.'
        : 'Get skill details. Returns parameters, confirm flag, and the invoke tool to use (cgw_invoke or cgw_invoke_confirmed). Always call this before invoking a skill for the first time.',
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
      description: config.confirmMode === 'otp'
        ? 'Call a corporate skill. Skills with confirm=true require a confirmation code — a 4-digit code will be shown to the user via browser notification. Ask the user for the code or to cancel, then call again with confirmCode.'
        : 'Call a corporate skill. Only for skills where confirm=false in cgw_schema. If confirm=true, use cgw_invoke_confirmed instead.',
      inputSchema: {
        type: 'object',
        properties: {
          skill: { type: 'string', description: 'Skill name.' },
          params: { type: 'object', description: 'Key-value parameters. All values as strings.', additionalProperties: { type: 'string' } },
          ...(config.confirmMode === 'otp' ? { confirmCode: { type: 'string', description: 'OTP confirmation code from the user.' } } : {})
        },
        required: ['skill']
      }
    },
    ...(config.confirmMode === 'native' ? [{
      name: 'cgw_invoke_confirmed',
      description: 'Call a corporate skill that requires confirmation (confirm=true in cgw_schema). Use this instead of cgw_invoke when the skill has confirm=true.',
      inputSchema: {
        type: 'object',
        properties: {
          skill: { type: 'string', description: 'Skill name.' },
          params: { type: 'object', description: 'Key-value parameters. All values as strings.', additionalProperties: { type: 'string' } }
        },
        required: ['skill']
      }
    }] : []),
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
      case 'cgw_invoke': text = await callInvoke(args, false); break;
      case 'cgw_invoke_confirmed': {
        const cfg = await getConfig();
        if (cfg.confirmMode !== 'native') throw new Error('cgw_invoke_confirmed is not available in OTP mode. Use cgw_invoke.');
        text = await callInvoke(args, true);
        break;
      }
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

  const config = await getConfig();
  const isConfirmed = skill.confirm === true;
  return JSON.stringify({
    name: skill.name,
    description: skill.description,
    confirm: isConfirmed,
    invoke: (isConfirmed && config.confirmMode === 'native') ? 'cgw_invoke_confirmed' : 'cgw_invoke',
    parameters: (skill.parameters || []).map(p => ({
      name: p.name,
      type: (p.type || 'String').toLowerCase(),
      required: p.required,
      description: p.description
    }))
  });
}

// ── Confirmation protocol ────────────────────────────────────
// OTP mode: one-time code shown via OS notification
// Native mode: agent uses cgw_invoke_confirmed (blocked here if wrong tool)

const pendingConfirmations = new Map(); // key → { code, expiresAt }
const CONFIRM_TTL = 60_000; // 60 seconds

function generateCode() {
  const arr = new Uint16Array(1);
  crypto.getRandomValues(arr);
  return String(arr[0] % 10000).padStart(4, '0');
}

async function confirmKey(skill, params) {
  const payload = skill + ':' + Object.keys(params).sort().map(k => `${k}=${params[k]}`).join('&');
  const data = new TextEncoder().encode(payload);
  const hash = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(hash)).map(b => b.toString(16).padStart(2, '0')).join('');
}

function needsConfirmation(skill) {
  return skill.confirm === true;
}

function showCodeNotification(skillName, params, code) {
  try {
    const truncate = (s, max = 50) => s.length > max ? s.slice(0, max) + '...' : s;
    const paramLines = Object.entries(params).map(([k, v]) => `  ${k}: ${truncate(String(v))}`).join('\n');
    const title = chrome.i18n.getMessage('confirmNotifTitle') || 'CorpGateway — confirm operation';
    const message = paramLines ? `${skillName}\n${paramLines}` : skillName;
    const contextMessage = chrome.i18n.getMessage('confirmNotifCode', [code])
      || `Code: ${code}`;
    chrome.notifications.create(`cgw-confirm-${Date.now()}`, {
      type: 'basic',
      iconUrl: chrome.runtime.getURL('icons/icon128.png'),
      title,
      message,
      contextMessage,
      priority: 2,
      requireInteraction: true
    });
  } catch (err) {
    console.warn('[CGW] Notification error:', err.message);
  }
}

async function callInvoke(args, confirmedTool = false) {
  const skillName = args.skill;
  if (!skillName) throw new Error('Missing required parameter: skill');

  const skill = await getSkillByName(skillName);
  if (!skill) throw new Error(`Skill not found: ${skillName}`);

  const confirmCode = args.confirmCode || args.params?.confirmCode;
  const params = { ...(args.params || {}) };
  delete params.confirmCode;

  // ── Confirmation gate ──
  // If skill requires confirmation AND agent used cgw_invoke (not cgw_invoke_confirmed):
  //   - confirmMode=native (default): hard block — agent must use cgw_invoke_confirmed
  //   - confirmMode=otp: OTP flow via OS notification
  if (needsConfirmation(skill) && !confirmedTool) {
    const config = await getConfig();
    if (config.confirmMode !== 'otp') {
      throw new Error(`Skill "${skillName}" requires confirmation. Use cgw_invoke_confirmed instead of cgw_invoke.`);
    }
    // Cleanup expired entries
    const now = Date.now();
    for (const [k, v] of pendingConfirmations) {
      if (now > v.expiresAt) pendingConfirmations.delete(k);
    }

    const key = await confirmKey(skillName, params);

    if (!confirmCode) {
      // Step 1: generate code, notify user, return message to agent
      const code = generateCode();
      pendingConfirmations.set(key, { code, expiresAt: now + CONFIRM_TTL });
      showCodeNotification(skillName, params, code);

      await writeAuditEntry({
        skill: skillName, params: Object.keys(params),
        error: 'confirmation_pending', durationMs: 0, ts: new Date().toISOString()
      });

      const msg = chrome.i18n.getMessage('confirmPending', [skillName])
        || `⚠ Write operation "${skillName}" requires confirmation. A 4-digit code was sent to the user via browser notification. Ask the user for the code and call cgw_invoke again with the same parameters plus confirmCode.`;
      return msg;
    }

    // Step 2: validate code
    const entry = pendingConfirmations.get(key);

    if (!entry || now > entry.expiresAt) {
      pendingConfirmations.delete(key);
      // Send a fresh code
      const newCode = generateCode();
      pendingConfirmations.set(key, { code: newCode, expiresAt: now + CONFIRM_TTL });
      showCodeNotification(skillName, params, newCode);

      throw new Error(chrome.i18n.getMessage('confirmExpired')
        || 'Confirmation code expired. A new code has been sent to the user.');
    }

    if (entry.code !== confirmCode) {
      throw new Error(chrome.i18n.getMessage('confirmInvalid')
        || 'Invalid confirmation code. Check the code in the browser notification and try again.');
    }

    // Code valid — consume it
    pendingConfirmations.delete(key);
  }

  // ── Execute skill ──
  const startTime = Date.now();
  let error = null;
  let result;
  try {
    result = await invokeSkill(skillName, params);
  } catch (err) {
    error = err.message;
    throw err;
  } finally {
    await writeAuditEntry({
      skill: skillName, params: Object.keys(params),
      confirmed: needsConfirmation(skill),
      error, durationMs: Date.now() - startTime, ts: new Date().toISOString()
    });
  }
  return JSON.stringify(result);
}

// ── Audit log ───────────────────────────────────────────────
// Stores last 100 invocations in chrome.storage.session (encrypted, cleared on restart)

const AUDIT_KEY = 'cgw_audit';
const AUDIT_MAX = 100;

async function writeAuditEntry(entry) {
  try {
    const data = await chrome.storage.session.get(AUDIT_KEY);
    const log = data[AUDIT_KEY] || [];
    log.push(entry);
    if (log.length > AUDIT_MAX) log.splice(0, log.length - AUDIT_MAX);
    await chrome.storage.session.set({ [AUDIT_KEY]: log });
  } catch {}
}

export async function getAuditLog() {
  try {
    const data = await chrome.storage.session.get(AUDIT_KEY);
    return data[AUDIT_KEY] || [];
  } catch { return []; }
}

function callHealth() {
  return JSON.stringify({ status: 'ok', runtime: 'extension' });
}

async function callAudit() {
  const log = await getAuditLog();
  return JSON.stringify({ entries: log, count: log.length });
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
