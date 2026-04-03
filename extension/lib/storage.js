// CorpGateway Extension — Storage layer
// CRUD for skills, groups, and config in chrome.storage.local

const STORE_KEY = 'cgw_store';
const CONFIG_KEY = 'cgw_config';

const DEFAULT_CONFIG = {
  bridgeUrl: 'http://localhost:9877',
  extensionToken: '',
  instanceName: '',  // e.g. "Chrome Work", "Chrome Personal"
  otpFallback: false  // if true, cgw_invoke uses OTP code flow for confirmed skills instead of blocking
};

// ── Store access ─────────────────────────────────────────────

async function getStore() {
  const data = await chrome.storage.local.get(STORE_KEY);
  return data[STORE_KEY] || { groups: [], skills: [] };
}

async function setStore(store) {
  await chrome.storage.local.set({ [STORE_KEY]: store });
}

async function getConfig() {
  const data = await chrome.storage.local.get(CONFIG_KEY);
  return { ...DEFAULT_CONFIG, ...data[CONFIG_KEY] };
}

async function setConfig(config) {
  await chrome.storage.local.set({ [CONFIG_KEY]: config });
}

// ── Groups ───────────────────────────────────────────────────

async function getGroups() {
  const store = await getStore();
  return store.groups;
}

async function getEnabledGroups() {
  const groups = await getGroups();
  return groups.filter(g => g.enabled !== false);
}

async function addGroup(group) {
  const store = await getStore();
  if (!group.id) group.id = crypto.randomUUID().slice(0, 8);
  if (group.enabled === undefined) group.enabled = true;
  store.groups.push(group);
  await setStore(store);
  return group;
}

async function updateGroup(id, updates) {
  const store = await getStore();
  const idx = store.groups.findIndex(g => g.id === id);
  if (idx >= 0) {
    store.groups[idx] = { ...store.groups[idx], ...updates };
    await setStore(store);
  }
}

async function deleteGroup(id) {
  const store = await getStore();
  store.groups = store.groups.filter(g => g.id !== id);
  store.skills = store.skills.filter(s => s.groupId !== id);
  await setStore(store);
}

// ── Skills ───────────────────────────────────────────────────

async function getSkills() {
  const store = await getStore();
  return store.skills;
}

async function getEnabledSkills() {
  const store = await getStore();
  const enabledGroupIds = new Set(
    store.groups.filter(g => g.enabled !== false).map(g => g.id)
  );
  return store.skills.filter(s => enabledGroupIds.has(s.groupId));
}

async function getSkillByName(name) {
  const skills = await getEnabledSkills();
  return skills.find(s => s.name.toLowerCase() === name.toLowerCase()) || null;
}

async function getSkillsByGroup(groupId) {
  const store = await getStore();
  return store.skills.filter(s => s.groupId === groupId);
}

async function addSkill(skill) {
  const store = await getStore();
  if (!skill.id) skill.id = crypto.randomUUID();
  skill.createdAt = new Date().toISOString();
  skill.updatedAt = skill.createdAt;
  store.skills.push(skill);
  await setStore(store);
  return skill;
}

async function updateSkill(id, updates) {
  const store = await getStore();
  const idx = store.skills.findIndex(s => s.id === id);
  if (idx >= 0) {
    store.skills[idx] = { ...store.skills[idx], ...updates, updatedAt: new Date().toISOString() };
    await setStore(store);
  }
}

async function deleteSkill(id) {
  const store = await getStore();
  store.skills = store.skills.filter(s => s.id !== id);
  await setStore(store);
}

// ── Import / Export ──────────────────────────────────────────

const VALID_HTTP_METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD'];
const VALID_PARAM_TYPES = ['String', 'Integer', 'Float', 'Boolean', 'Date'];

function validateSkillData(s) {
  const url = s.Url || s.url || '';
  if (url) {
    try {
      const parsed = new URL(url.replace(/\{[^}]+\}/g, 'placeholder'));
      if (!['http:', 'https:'].includes(parsed.protocol)) {
        throw new Error(`Unsupported protocol in URL: ${parsed.protocol}`);
      }
    } catch (e) {
      if (e.message.includes('protocol')) throw e;
      throw new Error(`Invalid skill URL: ${url}`);
    }
  }
  const method = (s.HttpMethod || s.httpMethod || 'GET').toUpperCase();
  if (!VALID_HTTP_METHODS.includes(method)) {
    throw new Error(`Invalid HTTP method: ${method}`);
  }
  const params = s.Parameters || s.parameters || [];
  if (params.length > 50) throw new Error('Too many parameters (max 50)');
  for (const p of params) {
    const type = p.Type || p.type || 'String';
    if (!VALID_PARAM_TYPES.includes(type)) {
      throw new Error(`Invalid parameter type: ${type}`);
    }
  }
  const headers = s.Headers || s.headers || {};
  if (Object.keys(headers).length > 20) throw new Error('Too many headers (max 20)');
}

async function importPreset(json) {
  const data = typeof json === 'string' ? JSON.parse(json) : json;
  const store = await getStore();
  let groupsAdded = 0, skillsAdded = 0, skipped = 0;
  const groupIdMap = {};

  for (const g of (data.Groups || data.groups || [])) {
    const existing = store.groups.find(
      x => x.name.toLowerCase() === g.Name?.toLowerCase() || x.name.toLowerCase() === g.name?.toLowerCase()
    );
    if (existing) {
      groupIdMap[g.Id || g.id] = existing.id;
      skipped++;
      continue;
    }
    const newId = crypto.randomUUID().slice(0, 8);
    groupIdMap[g.Id || g.id] = newId;
    store.groups.push({
      id: newId,
      name: g.Name || g.name || '',
      description: g.Description || g.description || '',
      color: g.Color || g.color || '#5B8DEF',
      enabled: g.Enabled !== undefined ? g.Enabled : (g.enabled !== undefined ? g.enabled : true)
    });
    groupsAdded++;
  }

  for (const s of (data.Skills || data.skills || [])) {
    const name = s.Name || s.name || '';
    if (store.skills.some(x => x.name.toLowerCase() === name.toLowerCase())) {
      skipped++;
      continue;
    }
    validateSkillData(s);
    const oldGroupId = s.GroupId || s.groupId || '';
    store.skills.push({
      id: crypto.randomUUID(),
      name,
      description: s.Description || s.description || '',
      groupId: groupIdMap[oldGroupId] || oldGroupId,
      url: s.Url || s.url || '',
      httpMethod: s.HttpMethod || s.httpMethod || 'GET',
      parameters: (s.Parameters || s.parameters || []).map(p => ({
        name: p.Name || p.name || '',
        description: p.Description || p.description || '',
        type: p.Type || p.type || 'String',
        required: p.Required !== undefined ? p.Required : (p.required !== undefined ? p.required : true),
        defaultValue: p.DefaultValue || p.defaultValue || null
      })),
      headers: s.Headers || s.headers || {},
      bodyTemplate: s.BodyTemplate || s.bodyTemplate || '',
      fetchOrigin: s.FetchOrigin || s.fetchOrigin || '',
      responseFilter: s.ResponseFilter || s.responseFilter || '',
      confirm: s.Confirm !== undefined ? s.Confirm : (s.confirm !== undefined ? s.confirm : undefined),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    });
    skillsAdded++;
  }

  await setStore(store);
  return { groupsAdded, skillsAdded, skipped };
}

async function exportStore() {
  return await getStore();
}

export {
  getStore, setStore, getConfig, setConfig,
  getGroups, getEnabledGroups, addGroup, updateGroup, deleteGroup,
  getSkills, getEnabledSkills, getSkillByName, getSkillsByGroup, addSkill, updateSkill, deleteSkill,
  importPreset, exportStore
};
