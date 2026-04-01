// CorpGateway Extension — Background Service Worker
// Connects to cgw_mcp via WebSocket — only when user explicitly starts connection

import { handleMcpRequest } from './lib/mcp.js';
import { setAuthHeader, cleanupInterceptors } from './lib/executor.js';
import { getConfig, getEnabledSkills } from './lib/storage.js';

const DEFAULT_BRIDGE_URL = 'http://localhost:9877'; // cgw_mcp server URL
let ws = null;
let connected = false;
let autoReconnect = false; // only reconnect if user started connection
let reconnectTimer = null;
let connecting = false;

// ── Crypto helper (Web Crypto API for Service Worker) ─────

async function hmacSHA256(key, message) {
  const enc = new TextEncoder();
  const cryptoKey = await crypto.subtle.importKey(
    'raw', enc.encode(key), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']
  );
  const sig = await crypto.subtle.sign('HMAC', cryptoKey, enc.encode(message));
  return Array.from(new Uint8Array(sig)).map(b => b.toString(16).padStart(2, '0')).join('');
}

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

    let serverAuthenticated = false;
    let challengeSent = false; // tracks that we answered a real challenge

    socket.onopen = () => {
      connecting = false;
      console.log('[CGW] WS открыт, ожидание challenge от сервера...');
    };

    socket.onmessage = async (event) => {
      try {
        const msg = JSON.parse(event.data);

        // ── Mutual auth: respond to server challenge ──
        if (!serverAuthenticated) {
          if (msg.type === 'challenge' && msg.challenge && msg.serverProof && !challengeSent) {
            // Verify server proof: HMAC-SHA256(extensionToken, "server:" + challenge)
            const expectedServerProof = await hmacSHA256(token, 'server:' + msg.challenge);
            if (expectedServerProof !== msg.serverProof) {
              console.warn('[CGW] Server proof invalid — not a genuine cgw_mcp server');
              socket.close();
              return;
            }
            // Server is authentic. Send client proof: HMAC-SHA256(extensionToken, "client:" + challenge)
            const hmac = await hmacSHA256(token, 'client:' + msg.challenge);
            socket.send(JSON.stringify({ type: 'challenge-response', hmac }));
            challengeSent = true;
            return;
          }
          if (msg.type === 'authenticated' && challengeSent) {
            // Only accept if we actually sent a challenge response first
            serverAuthenticated = true;
            ws = socket;
            connected = true;
            autoReconnect = true;
            startKeepalive();
            console.log('[CGW] Подключено к cgw_mcp (mutual auth OK)');
            return;
          }
          // Unexpected message or wrong sequence — server is not genuine
          console.warn('[CGW] Invalid auth sequence from server, closing');
          socket.close();
          return;
        }

        // ── Normal message processing (only after mutual auth) ──
        const response = await handleMcpRequest(msg);
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
      stopKeepalive();
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
  stopKeepalive();
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

// ── WebSocket keepalive ──────────────────────────────────────
// Sends ping every 25s to:
// 1. Prevent server from closing idle connection
// 2. Keep Service Worker alive (each message resets the 30s SW idle timer)

let keepaliveTimer = null;

function startKeepalive() {
  stopKeepalive();
  keepaliveTimer = setInterval(() => {
    if (ws && ws.readyState === WebSocket.OPEN) {
      try {
        ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'ping' }));
      } catch {}
    }
  }, 25000);
}

function stopKeepalive() {
  if (keepaliveTimer) {
    clearInterval(keepaliveTimer);
    keepaliveTimer = null;
  }
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

function isExtensionSender(sender) {
  // Accept messages only from extension's own pages (popup, options, service worker)
  // and extension's own content scripts
  if (sender.id !== chrome.runtime.id) return false;
  // Extension pages (popup.html, options.html) have url starting with chrome-extension://
  if (sender.url && sender.url.startsWith(chrome.runtime.getURL(''))) return true;
  // Content scripts injected by this extension have sender.id match but no extension URL
  // They should only access getStatus (read-only), not sensitive operations
  return false;
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  // getStatus is safe to expose to content scripts (read-only, used by overlay)
  if (msg.type === 'getStatus') {
    getConfig().then(cfg => {
      sendResponse({ connected, autoReconnect, instanceName: cfg.instanceName || '' });
    });
    return true;
  }

  // All operations below require sender to be extension's own pages
  if (!isExtensionSender(sender)) {
    console.warn('[CGW] Rejected message from untrusted sender:', sender.url || sender.id);
    return false;
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

  if (msg.type === 'skillsChanged') {
    // Cleanup interceptors for origins that no longer have active skills
    runInterceptorCleanup();
    sendResponse({ ok: true });
    return true;
  }
});

// ── Icon status indicator ───────────────────────────────────

const ICON_ACTIVE = { 16: 'icons/icon16.png', 48: 'icons/icon48.png', 128: 'icons/icon128.png' };
const ICON_GRAY = { 16: 'icons/icon16_gray.png', 48: 'icons/icon48_gray.png', 128: 'icons/icon128_gray.png' };
let lastIconState = null;

function updateIcon() {
  // Check actual WebSocket state, not just the `connected` flag
  const isAlive = ws !== null && ws.readyState === WebSocket.OPEN;
  if (isAlive !== connected) connected = isAlive;

  const state = connected ? 'active' : 'gray';
  if (state === lastIconState) return;
  lastIconState = state;

  chrome.action.setIcon({ path: connected ? ICON_ACTIVE : ICON_GRAY });
  chrome.action.setTitle({ title: connected ? 'CorpGateway — MCP подключён' : 'CorpGateway — не подключён' });

  // Broadcast overlay state to all tabs
  chrome.tabs.query({}, (tabs) => {
    for (const tab of tabs) {
      if (!tab.id || tab.id < 0) continue;
      // skip chrome:// and other restricted URLs where content scripts can't run
      if (tab.url && (tab.url.startsWith('chrome://') || tab.url.startsWith('chrome-extension://') || tab.url.startsWith('edge://'))) continue;
      chrome.tabs.sendMessage(tab.id, { type: 'cgw-overlay', connected }).catch(() => {});
    }
  });
}

setInterval(updateIcon, 1000);
updateIcon();

// ── Interceptor cleanup ──────────────────────────────────────
// Collects active origins from enabled skills and removes interceptors
// for origins that are no longer needed (skill/group deleted or disabled)

async function runInterceptorCleanup() {
  try {
    const skills = await getEnabledSkills();
    const activeOrigins = new Set();
    for (const s of skills) {
      try {
        const origin = s.fetchOrigin || new URL(s.url).origin;
        activeOrigins.add(origin);
      } catch {}
    }
    await cleanupInterceptors(activeOrigins);
  } catch (err) {
    console.log('[CGW] Interceptor cleanup error:', err.message);
  }
}

// ── Startup — do NOT auto-connect ──────────────────────────

console.log('[CGW] Service worker запущен (ожидание подключения)');
