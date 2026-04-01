// CorpGateway Extension — Background Service Worker
// Connects to cgw_mcp via WebSocket — only when user explicitly starts connection

import { handleMcpRequest } from './lib/mcp.js';
import { setAuthHeader } from './lib/executor.js';
import { getConfig } from './lib/storage.js';

const DEFAULT_BRIDGE_URL = 'http://localhost:9877';
let ws = null;
let connected = false;
let autoReconnect = false; // only reconnect if user started connection
let reconnectTimer = null;
let connecting = false;

// ── WebSocket connection ───────────────────────────────────

async function connect() {
  if (connecting) return;
  connecting = true;
  clearTimeout(reconnectTimer);

  if (ws) {
    const old = ws;
    ws = null;
    try { old.onclose = null; old.close(); } catch {}
  }

  const config = await getConfig();
  const bridgeUrl = config.bridgeUrl || DEFAULT_BRIDGE_URL;
  const token = config.extensionToken || '';

  if (!token) {
    console.log('[CGW] Нет токена расширения');
    connecting = false;
    return;
  }

  const name = encodeURIComponent(config.instanceName || 'default');
  const wsUrl = bridgeUrl.replace(/^http/, 'ws') + `/extension/ws?token=${token}&name=${name}`;
  console.log(`[CGW] Подключение к ${bridgeUrl} как "${config.instanceName || 'default'}"...`);

  try {
    const socket = new WebSocket(wsUrl);

    socket.onopen = () => {
      ws = socket;
      connected = true;
      connecting = false;
      autoReconnect = true; // after successful connect, enable auto-reconnect
      console.log('[CGW] Подключено к cgw_mcp');
    };

    socket.onmessage = async (event) => {
      try {
        const request = JSON.parse(event.data);
        const response = await handleMcpRequest(request);
        if (response && socket.readyState === WebSocket.OPEN) {
          socket.send(JSON.stringify(response));
        }
      } catch (err) {
        console.log('[CGW] Ошибка обработки:', err.message);
      }
    };

    socket.onclose = () => {
      if (ws === socket) { ws = null; connected = false; }
      connecting = false;
      console.log('[CGW] WebSocket закрыт');
      if (autoReconnect) scheduleReconnect();
    };

    socket.onerror = () => {
      connecting = false;
      connected = false;
    };
  } catch (err) {
    connecting = false;
    console.log('[CGW] Ошибка подключения:', err.message);
    if (autoReconnect) scheduleReconnect();
  }
}

function disconnect() {
  autoReconnect = false;
  clearTimeout(reconnectTimer);
  if (ws) {
    const old = ws;
    ws = null;
    connected = false;
    try { old.onclose = null; old.close(); } catch {}
  }
  connected = false;
  connecting = false;
  console.log('[CGW] Отключено');
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, 3000);
}

// ── Auth header interception ───────────────────────────────

chrome.webRequest.onSendHeaders.addListener(
  (details) => {
    if (!details.requestHeaders) return;
    for (const header of details.requestHeaders) {
      if (header.name.toLowerCase() === 'authorization' && header.value) {
        try {
          const origin = new URL(details.url).origin;
          setAuthHeader(origin, header.value);
        } catch {}
      }
    }
  },
  { urls: ['https://*/*', 'http://*/*'] },
  ['requestHeaders', 'extraHeaders']
);

// ── Message handler for popup/options ──────────────────────

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === 'getStatus') {
    getConfig().then(cfg => {
      sendResponse({ connected, autoReconnect, instanceName: cfg.instanceName || '' });
    });
    return true;
  }

  if (msg.type === 'connect') {
    connect();
    sendResponse({ ok: true });
    return true;
  }

  if (msg.type === 'disconnect') {
    disconnect();
    sendResponse({ ok: true });
    return true;
  }

  if (msg.type === 'configUpdated') {
    // If was connected, reconnect with new config
    if (autoReconnect) {
      disconnect();
      setTimeout(connect, 500);
    }
    sendResponse({ ok: true });
    return true;
  }

  if (msg.type === 'mcpRequest') {
    handleMcpRequest(msg.request).then(response => {
      sendResponse(response);
    });
    return true;
  }
});

// ── Icon status indicator ───────────────────────────────────

const ICON_ACTIVE = { 16: 'icons/icon16.png', 48: 'icons/icon48.png', 128: 'icons/icon128.png' };
const ICON_GRAY = { 16: 'icons/icon16_gray.png', 48: 'icons/icon48_gray.png', 128: 'icons/icon128_gray.png' };
let lastIconState = null;

function updateIcon() {
  const state = connected ? 'active' : 'gray';
  if (state === lastIconState) return;
  lastIconState = state;

  chrome.action.setIcon({ path: connected ? ICON_ACTIVE : ICON_GRAY });
  chrome.action.setTitle({ title: connected ? 'CorpGateway — MCP подключён' : 'CorpGateway — не подключён' });
}

setInterval(updateIcon, 1000);
updateIcon();

// ── Startup — do NOT auto-connect ──────────────────────────

console.log('[CGW] Service worker запущен (ожидание подключения)');
