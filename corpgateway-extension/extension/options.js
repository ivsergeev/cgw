import {
  getConfig, setConfig, getGroups, getSkills, getSkillsByGroup,
  addGroup, updateGroup, deleteGroup,
  addSkill, updateSkill, deleteSkill,
  importPreset, exportStore
} from './lib/storage.js';

// ── State ──────────────────────────────────────────────────

let selectedGroupId = null;   // null = all groups
let selectedSkillId = null;
let panelMode = '';           // '' | 'editSkill' | 'editGroup' | 'test' | 'settings'
let confirmCallback = null;
let allGroups = [];
let allSkills = [];

// ── Init ───────────────────────────────────────────────────

async function init() {
  await refreshData();
  renderGroups();
  renderSkills();
  renderPanel();
  checkBridgeStatus();
}

async function refreshData() {
  allGroups = await getGroups();
  allSkills = await getSkills();
}

// ── Bridge status ──────────────────────────────────────────

function checkBridgeStatus() {
  chrome.runtime.sendMessage({ type: 'getStatus' }, (status) => {
    const dot = document.getElementById('bridgeDot');
    const text = document.getElementById('bridgeStatus');
    dot.className = status?.connected ? 'dot on' : 'dot off';
    text.textContent = status?.connected ? 'Bridge: connected' : 'Bridge: disconnected';
  });
  setTimeout(checkBridgeStatus, 5000);
}

// ── Sidebar: Groups ────────────────────────────────────────

function renderGroups() {
  const container = document.getElementById('groupList');
  container.innerHTML = '';

  // "All" item
  const allItem = el('div', { className: `group-item ${selectedGroupId === null ? 'selected' : ''}`, onclick: () => { selectedGroupId = null; renderGroups(); renderSkills(); } });
  allItem.innerHTML = `<span class="name" style="font-weight:500">Все скилы</span>`;
  container.appendChild(allItem);

  for (const g of allGroups) {
    const item = el('div', {
      className: `group-item ${g.id === selectedGroupId ? 'selected' : ''} ${g.enabled === false ? 'disabled' : ''}`,
      onclick: () => { selectedGroupId = g.id; renderGroups(); renderSkills(); }
    });
    item.innerHTML = `
      <input type="checkbox" ${g.enabled !== false ? 'checked' : ''} style="flex-shrink:0">
      <span class="name">${esc(g.name)}</span>
      <span class="actions">
        <button class="btn-icon edit-group" title="Edit">✎</button>
        <button class="btn-icon del-group" title="Delete">✕</button>
      </span>
    `;
    item.querySelector('input').addEventListener('click', async (e) => {
      e.stopPropagation();
      await updateGroup(g.id, { enabled: e.target.checked });
      await refreshData();
      renderGroups();
      notifySkillsChanged();
    });
    item.querySelector('.edit-group').addEventListener('click', (e) => {
      e.stopPropagation();
      openEditGroup(g.id);
    });
    item.querySelector('.del-group').addEventListener('click', (e) => {
      e.stopPropagation();
      showConfirm(`Удалить группу «${g.name}» и все её скилы?`, async () => {
        await deleteGroup(g.id);
        if (selectedGroupId === g.id) selectedGroupId = null;
        await refreshData();
        renderGroups();
        renderSkills();
        clearPanel();
        notifySkillsChanged();
      });
    });
    container.appendChild(item);
  }
}

// ── Center: Skills List ────────────────────────────────────

function renderSkills() {
  const container = document.getElementById('skillList');
  const search = document.getElementById('searchInput').value.toLowerCase();
  container.innerHTML = '';

  let skills = selectedGroupId
    ? allSkills.filter(s => s.groupId === selectedGroupId)
    : allSkills;

  if (search) {
    skills = skills.filter(s =>
      s.name.toLowerCase().includes(search) ||
      (s.description || '').toLowerCase().includes(search)
    );
  }

  for (const s of skills) {
    const card = el('div', {
      className: `skill-card ${s.id === selectedSkillId ? 'selected' : ''}`,
      onclick: () => openEditSkill(s.id)
    });
    card.innerHTML = `
      <div class="info">
        <div class="top">
          <span class="method-badge">${esc(s.httpMethod)}</span>
          <span class="skill-name">${esc(s.name)}</span>
        </div>
        <div class="sig">${esc(compactSig(s))}</div>
        ${s.description ? `<div class="desc">${esc(s.description)}</div>` : ''}
      </div>
      <div class="card-actions">
        <button class="btn btn-ghost btn-sm test-btn" style="color:#4f7ef7">▶ Тест</button>
        <button class="btn-icon del-btn">✕</button>
      </div>
    `;
    card.querySelector('.test-btn').addEventListener('click', (e) => { e.stopPropagation(); openTest(s.id); });
    card.querySelector('.del-btn').addEventListener('click', (e) => {
      e.stopPropagation();
      showConfirm(`Удалить скил «${s.name}»?`, async () => {
        await deleteSkill(s.id);
        if (selectedSkillId === s.id) clearPanel();
        await refreshData();
        renderSkills();
        notifySkillsChanged();
      });
    });
    container.appendChild(card);
  }
}

// ── Panel ──────────────────────────────────────────────────

function renderPanel() {
  document.getElementById('emptyState').classList.toggle('hidden', panelMode !== '');
  document.getElementById('panelContent').classList.toggle('hidden', panelMode === '');
}

function clearPanel() {
  selectedSkillId = null;
  panelMode = '';
  document.getElementById('panelContent').innerHTML = '';
  renderPanel();
  renderSkills();
}

// ── Edit Group Panel ───────────────────────────────────────

function openEditGroup(groupId) {
  const g = allGroups.find(x => x.id === groupId);
  if (!g) return;
  selectedSkillId = null;
  panelMode = 'editGroup';
  renderPanel();

  const pc = document.getElementById('panelContent');
  pc.innerHTML = `
    <div class="panel-title">Группа</div>
    <div class="section-title">Основное</div>
    <label>Название</label>
    <input type="text" id="gName" value="${esc(g.name)}">
    <label>Описание (видно агенту как заголовок группы)</label>
    <input type="text" id="gDesc" value="${esc(g.description || '')}" placeholder="напр. Jira — управление задачами">
    <label style="display:flex;align-items:center;gap:8px;margin:8px 0 16px">
      <input type="checkbox" id="gEnabled" ${g.enabled !== false ? 'checked' : ''}>
      <span style="font-size:13px">Группа активна (скилы доступны агентам)</span>
    </label>
    <div class="flex">
      <button class="btn btn-primary" id="gSave">Сохранить</button>
      <button class="btn btn-ghost" id="gCancel">Отмена</button>
    </div>
  `;
  pc.querySelector('#gSave').addEventListener('click', async () => {
    await updateGroup(groupId, {
      name: pc.querySelector('#gName').value.trim(),
      description: pc.querySelector('#gDesc').value.trim(),
      enabled: pc.querySelector('#gEnabled').checked
    });
    await refreshData();
    renderGroups();
    toast('Группа сохранена', 'success');
  });
  pc.querySelector('#gCancel').addEventListener('click', clearPanel);
  renderSkills();
}

// ── Edit Skill Panel ───────────────────────────────────────

function openEditSkill(skillId) {
  const s = allSkills.find(x => x.id === skillId);
  if (!s) return;
  selectedSkillId = skillId;
  panelMode = 'editSkill';
  renderPanel();

  const groupOptions = allGroups.map(g => `<option value="${g.id}" ${g.id === s.groupId ? 'selected' : ''}>${esc(g.name)}</option>`).join('');
  const methodOptions = ['GET','POST','PUT','PATCH','DELETE'].map(m => `<option ${m === s.httpMethod ? 'selected' : ''}>${m}</option>`).join('');
  const isBody = ['POST','PUT','PATCH'].includes(s.httpMethod);

  const pc = document.getElementById('panelContent');
  pc.innerHTML = `
    <div class="panel-title">Настройка скила</div>

    <div class="section-title">Основное</div>
    <label>Название (имя функции)</label>
    <input type="text" id="sName" value="${esc(s.name)}" placeholder="напр. get_employee">
    <label>Описание (видно агенту)</label>
    <input type="text" id="sDesc" value="${esc(s.description || '')}" placeholder="Что возвращает этот скил?">

    <div class="section-title">Запрос</div>
    <label>URL эндпоинта</label>
    <input type="text" id="sUrl" value="${esc(s.url)}" class="mono" placeholder="https://api.corp.com/resource/{id}">
    <label>Origin URL (необяз.)</label>
    <input type="text" id="sOrigin" value="${esc(s.fetchOrigin || '')}" class="mono" placeholder="https://app.corp.com">
    <div class="hint">Откуда выполнять запрос, если отличается от URL</div>
    <div class="row">
      <div>
        <label>HTTP метод</label>
        <select id="sMethod">${methodOptions}</select>
      </div>
      <div>
        <label>Группа</label>
        <select id="sGroup">${groupOptions}</select>
      </div>
    </div>
    <div id="bodySection" class="${isBody ? '' : 'hidden'}">
      <label>Шаблон тела запроса (JSON, {{param}})</label>
      <textarea id="sBody" class="mono" rows="3">${esc(s.bodyTemplate || '')}</textarea>
      <div class="hint">Параметры как {{имя}}. Если пусто — отправляются как плоский JSON</div>
    </div>

    <div class="section-title">Ответ</div>
    <label>Фильтр полей (необяз.)</label>
    <input type="text" id="sFilter" value="${esc(s.responseFilter || '')}" class="mono" placeholder="key, fields.summary, fields.status.name">
    <div class="hint">Через запятую, dot-notation. Если пусто — полный ответ</div>

    <div class="section-title">HTTP заголовки</div>
    <div id="headersList">
      ${Object.entries(s.headers || {}).map(([k,v]) => headerRowHtml(k, v)).join('')}
    </div>
    <button class="btn btn-ghost btn-sm" id="addHeaderBtn" style="margin-bottom:12px">+ Заголовок</button>

    <div class="section-title">Параметры</div>
    <div id="paramsList">
      ${(s.parameters || []).map(p => paramCardHtml(p)).join('')}
    </div>
    <button class="btn btn-ghost btn-sm" id="addParamBtn" style="margin-bottom:12px">+ Параметр</button>

    <div class="sig-preview">
      <div class="label">Сигнатура для агента</div>
      <code id="sigPreview">${esc(compactSig(s))}</code>
    </div>

    <div class="flex">
      <button class="btn btn-primary" id="sSave">Сохранить скил</button>
      <button class="btn btn-ghost" id="sCancel">Отмена</button>
    </div>
  `;

  // Method change → toggle body section
  pc.querySelector('#sMethod').addEventListener('change', (e) => {
    pc.querySelector('#bodySection').classList.toggle('hidden', !['POST','PUT','PATCH'].includes(e.target.value));
  });

  // Headers
  pc.querySelector('#addHeaderBtn').addEventListener('click', () => {
    pc.querySelector('#headersList').insertAdjacentHTML('beforeend', headerRowHtml('', ''));
    bindHeaderDeletes(pc);
  });
  bindHeaderDeletes(pc);

  // Parameters
  pc.querySelector('#addParamBtn').addEventListener('click', () => {
    pc.querySelector('#paramsList').insertAdjacentHTML('beforeend', paramCardHtml({ name: '', type: 'String', required: true, description: '' }));
    bindParamDeletes(pc);
    updateSignature(pc);
  });
  bindParamDeletes(pc);

  // Live signature update
  for (const id of ['sName']) {
    pc.querySelector(`#${id}`).addEventListener('input', () => updateSignature(pc));
  }

  // Save
  pc.querySelector('#sSave').addEventListener('click', async () => {
    const updates = collectSkillData(pc);
    await updateSkill(skillId, updates);
    await refreshData();
    renderSkills();
    toast('Скил сохранён', 'success');
    openEditSkill(skillId); // refresh panel
  });
  pc.querySelector('#sCancel').addEventListener('click', clearPanel);
  renderSkills();
}

// ── Test Panel ─────────────────────────────────────────────

function openTest(skillId) {
  const s = allSkills.find(x => x.id === skillId);
  if (!s) return;
  selectedSkillId = skillId;
  panelMode = 'test';
  renderPanel();

  const pc = document.getElementById('panelContent');
  pc.innerHTML = `
    <div class="panel-title">Тест: ${esc(s.name)}</div>
    <div id="testParams">
      ${(s.parameters || []).map(p => `
        <div style="margin-bottom:10px">
          <div class="flex" style="margin-bottom:4px">
            <label style="margin:0;font-weight:600;color:#374151">${esc(p.name)}</label>
            <span class="method-badge" style="font-size:9px">${(p.type || 'String').toLowerCase()}</span>
            ${p.required ? '<span style="background:#fef2f2;color:#ef4444;padding:2px 5px;border-radius:4px;font-size:10px">req</span>' : ''}
          </div>
          <input type="text" data-param="${esc(p.name)}" placeholder="${esc(p.description || p.name)}" style="margin-bottom:0">
        </div>
      `).join('')}
    </div>
    <div class="flex" style="margin:12px 0">
      <button class="btn btn-primary" id="runTest">▶ Запустить</button>
      <button class="btn btn-ghost" id="testBack">Назад</button>
    </div>
    <label>Ответ</label>
    <div class="test-response"><pre id="testResult">Нажмите «Запустить» для выполнения</pre></div>
  `;

  pc.querySelector('#runTest').addEventListener('click', async () => {
    const pre = pc.querySelector('#testResult');
    pre.textContent = 'Выполняется...';

    const params = {};
    pc.querySelectorAll('[data-param]').forEach(el => {
      if (el.value.trim()) params[el.dataset.param] = el.value.trim();
    });

    try {
      const response = await chrome.runtime.sendMessage({
        type: 'mcpRequest',
        request: {
          jsonrpc: '2.0', id: Date.now(),
          method: 'tools/call',
          params: { name: 'cgw_invoke', arguments: { skill: s.name, params } }
        }
      });
      const text = response?.result?.content?.[0]?.text || JSON.stringify(response, null, 2);
      try { pre.textContent = JSON.stringify(JSON.parse(text), null, 2); }
      catch { pre.textContent = text; }
    } catch (err) {
      pre.textContent = `Error: ${err.message}`;
    }
  });
  pc.querySelector('#testBack').addEventListener('click', () => openEditSkill(skillId));
  renderSkills();
}

// ── Settings Panel ─────────────────────────────────────────

async function openSettings() {
  selectedSkillId = null;
  panelMode = 'settings';
  renderPanel();

  const config = await getConfig();
  const pc = document.getElementById('panelContent');
  pc.innerHTML = `
    <div class="panel-title">Настройки</div>

    <div class="section-title">Подключение к MCP</div>
    <label>Имя экземпляра</label>
    <input type="text" id="cfgName" value="${esc(config.instanceName || '')}" placeholder="напр. Chrome Рабочий">
    <div class="hint">Для идентификации при нескольких браузерах</div>

    <label>URL Bridge</label>
    <input type="text" id="cfgBridgeUrl" class="mono" value="${esc(config.bridgeUrl || 'http://localhost:9877')}" placeholder="http://localhost:9877">
    <div class="hint">HTTP-адрес демона bridge</div>

    <label>Токен расширения</label>
    <div class="token-row">
      <input type="text" id="cfgExtToken" class="mono" value="${esc(config.extensionToken || '')}" placeholder="From ~/.corpgateway/bridge.json → extensionToken">
      <button class="btn btn-ghost btn-sm" id="copyExtToken">Копировать</button>
    </div>
    <div class="hint">Скопируйте extensionToken из ~/.corpgateway/bridge.json</div>

    <div class="hint" style="margin-top:8px;padding:10px;background:#f0f4ff;border-radius:8px;color:#374151">
      Токен агента и MCP-инструкция настраиваются в <code style="background:#e5e7eb;padding:2px 4px;border-radius:4px">~/.corpgateway/bridge.json</code>
    </div>

    <div class="flex" style="margin-top:16px">
      <button class="btn btn-primary" id="cfgSave">Сохранить</button>
      <button class="btn btn-ghost" id="cfgClose">Закрыть</button>
    </div>
  `;

  pc.querySelector('#copyExtToken').addEventListener('click', () => {
    navigator.clipboard.writeText(pc.querySelector('#cfgExtToken').value);
    toast('Скопировано', 'success');
  });
  pc.querySelector('#cfgSave').addEventListener('click', async () => {
    const c = await getConfig();
    c.instanceName = pc.querySelector('#cfgName').value.trim();
    c.bridgeUrl = pc.querySelector('#cfgBridgeUrl').value.trim();
    c.extensionToken = pc.querySelector('#cfgExtToken').value.trim();
    await setConfig(c);
    chrome.runtime.sendMessage({ type: 'configUpdated' }, (status) => {
      // configUpdated handler returns { ok: true }
    });
    chrome.runtime.sendMessage({ type: 'getStatus' }, (status) => {
      const msg = status?.autoReconnect
        ? 'Настройки сохранены — переподключение'
        : 'Настройки сохранены';
      toast(msg, 'success');
    });
  });
  pc.querySelector('#cfgClose').addEventListener('click', clearPanel);
  renderSkills();
}

// ── Helpers: HTML generators ───────────────────────────────

function headerRowHtml(k, v) {
  return `<div class="header-row">
    <input type="text" value="${esc(k)}" placeholder="Header name" class="mono" style="flex:1;margin:0" data-hk>
    <input type="text" value="${esc(v)}" placeholder="Value (supports {{param}})" class="mono" style="flex:2;margin:0" data-hv>
    <button class="btn-icon del-header">✕</button>
  </div>`;
}

function paramCardHtml(p) {
  const typeOpts = ['String','Integer','Float','Boolean','Date'].map(t => `<option ${t === (p.type || 'String') ? 'selected' : ''}>${t}</option>`).join('');
  return `<div class="param-card">
    <div class="param-row">
      <input type="text" value="${esc(p.name)}" placeholder="param_name" style="flex:1;margin:0" data-pn>
      <select data-pt style="width:100px;margin:0">${typeOpts}</select>
      <label style="display:flex;align-items:center;gap:4px;margin:0;white-space:nowrap">
        <input type="checkbox" ${p.required ? 'checked' : ''} data-pr> Req
      </label>
      <button class="btn-icon del-param">✕</button>
    </div>
    <div class="param-row">
      <input type="text" value="${esc(p.description || '')}" placeholder="Description" style="flex:1;margin:0" data-pd>
    </div>
  </div>`;
}

function bindHeaderDeletes(pc) {
  pc.querySelectorAll('.del-header').forEach(btn => {
    btn.onclick = () => btn.closest('.header-row').remove();
  });
}

function bindParamDeletes(pc) {
  pc.querySelectorAll('.del-param').forEach(btn => {
    btn.onclick = () => { btn.closest('.param-card').remove(); updateSignature(pc); };
  });
}

function updateSignature(pc) {
  const name = pc.querySelector('#sName')?.value || 'skill_name';
  const params = collectParams(pc);
  const typeMap = { String: 'str', Integer: 'int', Float: 'float', Boolean: 'bool', Date: 'date' };
  const parts = params.map(p => `${p.name}:${typeMap[p.type] || 'str'}${p.required ? '' : '?'}`);
  const sig = pc.querySelector('#sigPreview');
  if (sig) sig.textContent = `${name}(${parts.join(', ')})`;
}

function collectSkillData(pc) {
  const headers = {};
  pc.querySelectorAll('.header-row').forEach(row => {
    const k = row.querySelector('[data-hk]').value.trim();
    const v = row.querySelector('[data-hv]').value.trim();
    if (k) headers[k] = v;
  });
  return {
    name: pc.querySelector('#sName').value.trim(),
    description: pc.querySelector('#sDesc').value.trim(),
    url: pc.querySelector('#sUrl').value.trim(),
    fetchOrigin: pc.querySelector('#sOrigin').value.trim(),
    httpMethod: pc.querySelector('#sMethod').value,
    groupId: pc.querySelector('#sGroup').value,
    bodyTemplate: pc.querySelector('#sBody')?.value?.trim() || '',
    responseFilter: pc.querySelector('#sFilter').value.trim(),
    headers,
    parameters: collectParams(pc)
  };
}

function collectParams(pc) {
  const params = [];
  pc.querySelectorAll('.param-card').forEach(card => {
    const name = card.querySelector('[data-pn]').value.trim();
    if (!name) return;
    params.push({
      name,
      type: card.querySelector('[data-pt]').value,
      required: card.querySelector('[data-pr]').checked,
      description: card.querySelector('[data-pd]').value.trim()
    });
  });
  return params;
}

function compactSig(s) {
  const typeMap = { String: 'str', Integer: 'int', Float: 'float', Boolean: 'bool', Date: 'date' };
  const params = (s.parameters || []).map(p => {
    const t = typeMap[p.type] || 'str';
    return p.required ? `${p.name}:${t}` : `${p.name}:${t}?`;
  });
  return `${s.name}(${params.join(', ')})`;
}

// ── Confirmation dialog ────────────────────────────────────

function showConfirm(msg, callback) {
  document.getElementById('confirmMessage').textContent = msg;
  document.getElementById('confirmOverlay').classList.remove('hidden');
  confirmCallback = callback;
}

document.getElementById('confirmYes').addEventListener('click', () => {
  document.getElementById('confirmOverlay').classList.add('hidden');
  confirmCallback?.();
  confirmCallback = null;
});
document.getElementById('confirmNo').addEventListener('click', () => {
  document.getElementById('confirmOverlay').classList.add('hidden');
  confirmCallback = null;
});

// ── Toast ──────────────────────────────────────────────────

function toast(msg, type) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.className = `toast ${type} show`;
  setTimeout(() => t.classList.remove('show'), 2500);
}

// ── Toolbar actions ────────────────────────────────────────

document.getElementById('searchInput').addEventListener('input', renderSkills);
document.getElementById('addGroupBtn').addEventListener('click', async () => {
  const g = await addGroup({ name: 'New Group', description: '', enabled: true });
  await refreshData();
  renderGroups();
  openEditGroup(g.id);
});
document.getElementById('addSkillBtn').addEventListener('click', async () => {
  const groupId = selectedGroupId || allGroups[0]?.id;
  if (!groupId) { toast('Сначала создайте группу', 'error'); return; }
  const s = await addSkill({ name: 'new_skill', description: '', groupId, url: '', httpMethod: 'GET', parameters: [], headers: {}, bodyTemplate: '', fetchOrigin: '', responseFilter: '' });
  await refreshData();
  renderSkills();
  openEditSkill(s.id);
});
document.getElementById('openSettingsBtn').addEventListener('click', openSettings);

document.getElementById('importBtn').addEventListener('click', () => document.getElementById('importFile').click());
document.getElementById('importFile').addEventListener('change', async (e) => {
  const file = e.target.files[0];
  if (!file) return;
  try {
    const result = await importPreset(await file.text());
    toast(`Импортировано: ${result.groupsAdded} групп, ${result.skillsAdded} скилов`, 'success');
    await refreshData();
    renderGroups();
    renderSkills();
  } catch (err) { toast(`Ошибка импорта: ${err.message}`, 'error'); }
  e.target.value = '';
});
document.getElementById('exportBtn').addEventListener('click', async () => {
  const store = await exportStore();
  const blob = new Blob([JSON.stringify(store, null, 2)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'corpgateway-export.json';
  a.click();
  URL.revokeObjectURL(a.href);
});

// ── Utils ──────────────────────────────────────────────────

function notifySkillsChanged() {
  chrome.runtime.sendMessage({ type: 'skillsChanged' }, () => {});
}

function el(tag, props) { const e = document.createElement(tag); Object.assign(e, props); return e; }
function esc(s) { return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
document.querySelector('.hidden')  // ensure CSS class exists

init();
