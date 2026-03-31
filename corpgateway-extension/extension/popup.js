import { getGroups, getSkills, updateGroup } from './lib/storage.js';

// ── Status ─────────────────────────────────────────────────

function updateStatus() {
  chrome.runtime.sendMessage({ type: 'getStatus' }, (status) => {
    const btn = document.getElementById('connectBtn');
    const label = document.getElementById('statusLabel');

    if (status?.connected) {
      btn.className = 'connect-btn active';
      btn.textContent = '⚡';
      btn.title = 'Отключить';
      const name = status.instanceName ? ` (${status.instanceName})` : '';
      label.textContent = `Подключён${name}`;
      label.className = 'status-label on';
    } else if (status?.autoReconnect) {
      btn.className = 'connect-btn';
      btn.textContent = '...';
      btn.title = 'Переподключение';
      label.textContent = 'Переподключение...';
      label.className = 'status-label';
    } else {
      btn.className = 'connect-btn';
      btn.textContent = '⚡';
      btn.title = 'Подключить';
      label.textContent = 'Не подключён';
      label.className = 'status-label';
    }
  });
}

updateStatus();
setInterval(updateStatus, 2000);

// ── Connect / Disconnect ───────────────────────────────────

document.getElementById('connectBtn').addEventListener('click', () => {
  chrome.runtime.sendMessage({ type: 'getStatus' }, (status) => {
    if (status?.connected) {
      chrome.runtime.sendMessage({ type: 'disconnect' });
    } else {
      chrome.runtime.sendMessage({ type: 'connect' });
    }
    setTimeout(updateStatus, 500);
  });
});

// ── Groups ─────────────────────────────────────────────────

async function loadGroups() {
  const groups = await getGroups();
  const skills = await getSkills();
  const container = document.getElementById('groups');
  container.innerHTML = '';

  if (groups.length === 0) {
    container.innerHTML = '<div class="empty">Нет групп. Откройте настройки.</div>';
    return;
  }

  for (const g of groups) {
    const count = skills.filter(s => s.groupId === g.id).length;
    const div = document.createElement('div');
    div.className = `group-card ${g.enabled === false ? 'disabled' : ''}`;
    div.innerHTML = `
      <label class="toggle">
        <input type="checkbox" ${g.enabled !== false ? 'checked' : ''}>
        <span class="slider"></span>
      </label>
      <div class="info">
        <div class="name">${esc(g.name)}</div>
        ${g.description ? `<div class="desc">${esc(g.description)}</div>` : ''}
      </div>
      <span class="count">${count}</span>
    `;
    div.querySelector('input').addEventListener('change', async (e) => {
      await updateGroup(g.id, { enabled: e.target.checked });
      loadGroups();
    });
    container.appendChild(div);
  }
}

// ── Actions ────────────────────────────────────────────────

document.getElementById('openOptions').addEventListener('click', () => {
  chrome.runtime.openOptionsPage();
});

// ── Init ───────────────────────────────────────────────────

loadGroups();

function esc(str) {
  return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
