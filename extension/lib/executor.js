// CorpGateway Extension — Skill executor with origin tab pool
//
// Mirrors ChromeCdpService flow from tray app:
// - Maintains a pool of background tabs (one per origin)
// - Injects fetch/XHR interceptor to capture Authorization headers
//   (mirrors CDP Network.requestWillBeSent)
// - Executes fetch() in MAIN world (mirrors CDP Runtime.evaluate)
// - On 401: reloads page, waits for new auth header, retries
// - On network error: evicts tab, creates fresh session, retries

import { getEnabledSkills } from './storage.js';

// ── Auth header cache ────────────────────────────────────────

const authCache = new Map(); // origin → Authorization header value

export function setAuthHeader(origin, value) {
  authCache.set(origin, value);
}

export function getAuthHeader(origin) {
  return authCache.get(origin) || null;
}

// ── Origin tab pool ────────────────────────────────────────

const originPool = new Map(); // origin → { tabId }
const POOL_TIMEOUT = 25000;
const AUTH_WAIT_TIMEOUT = 10000;
const AUTH_REFRESH_TIMEOUT = 15000;

// Lock to prevent concurrent tab creation for the same origin (mirrors CDP _poolLock)
const poolLocks = new Map();

async function withPoolLock(origin, fn) {
  while (poolLocks.has(origin)) {
    await poolLocks.get(origin);
  }
  let resolve;
  const lock = new Promise(r => { resolve = r; });
  poolLocks.set(origin, lock);
  try {
    return await fn();
  } finally {
    poolLocks.delete(origin);
    resolve();
  }
}

// ── Auth interceptor registration ────────────────────────────
// Registers a content script that patches fetch/XHR in MAIN world
// at document_start — BEFORE any page scripts run.
// This mirrors CDP Network.enable + Network.requestWillBeSent.

const registeredOrigins = new Set();

function interceptorScriptId(origin) {
  return 'cgw-auth-' + origin.replace(/[^a-z0-9]/gi, '_');
}

async function ensureAuthInterceptor(origin) {
  if (registeredOrigins.has(origin)) return;

  const scriptId = interceptorScriptId(origin);
  const urlPattern = origin + '/*';

  try {
    await chrome.scripting.unregisterContentScripts({ ids: [scriptId] });
  } catch {}

  await chrome.scripting.registerContentScripts([{
    id: scriptId,
    matches: [urlPattern],
    js: ['lib/auth-interceptor.js'],
    runAt: 'document_start',
    world: 'MAIN',
    allFrames: false
  }]);

  registeredOrigins.add(origin);
  console.log(`[CGW] Auth interceptor registered for ${origin}`);
}

// Unregister interceptor for an origin (called on evict / cleanup)
async function unregisterAuthInterceptor(origin) {
  if (!registeredOrigins.has(origin)) return;

  const scriptId = interceptorScriptId(origin);
  try {
    await chrome.scripting.unregisterContentScripts({ ids: [scriptId] });
  } catch {}

  registeredOrigins.delete(origin);
  authCache.delete(origin);
  console.log(`[CGW] Auth interceptor unregistered for ${origin}`);
}

// Cleanup all interceptors for origins that no longer have active skills.
// Call this after skill/group deletion to avoid stale registrations.
export async function cleanupInterceptors(activeOrigins) {
  for (const origin of registeredOrigins) {
    if (!activeOrigins.has(origin)) {
      await evictOriginTab(origin);
      await unregisterAuthInterceptor(origin);
    }
  }
}

// Read captured auth from origin tab's window.__cgw_auth
async function readCapturedAuth(tabId, origin) {
  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId },
      world: 'MAIN',
      func: () => window.__cgw_auth || null
    });
    const auth = results?.[0]?.result;
    if (auth) {
      authCache.set(origin, auth);
      return auth;
    }
  } catch {}
  return null;
}

// Close orphaned pinned tabs on an origin that are not tracked in the pool.
// This prevents tab duplication when evict+recreate races or SW restarts.
async function closeOrphanedTabs(origin) {
  try {
    const tabs = await chrome.tabs.query({ pinned: true });
    const pooledTabId = originPool.get(origin)?.tabId;
    for (const tab of tabs) {
      if (tab.id === pooledTabId) continue; // don't close pooled tab
      try {
        if (tab.url && new URL(tab.url).origin.toLowerCase() === origin.toLowerCase()) {
          console.log(`[CGW] Closing orphaned tab ${tab.id} on ${origin}`);
          await chrome.tabs.remove(tab.id);
        }
      } catch {}
    }
  } catch {}
}

// ── Origin tab management ────────────────────────────────────

async function getOrCreateOriginTab(origin) {
  return withPoolLock(origin, async () => {
    // Check existing tab
    const existing = originPool.get(origin);
    if (existing) {
      try {
        const tab = await chrome.tabs.get(existing.tabId);
        if (tab && !tab.url?.startsWith('chrome://')) {
          // Tab exists — try to read fresh auth from it
          await readCapturedAuth(existing.tabId, origin);
          return existing.tabId;
        }
      } catch {
        originPool.delete(origin);
      }
    }

    // Close any orphaned pinned tabs on this origin (prevents duplicates)
    await closeOrphanedTabs(origin);

    // Register auth interceptor BEFORE creating tab
    // (runs at document_start, captures auth from SPA's initial requests)
    await ensureAuthInterceptor(origin);

    // Create new background tab
    console.log(`[CGW] Opening origin tab: ${origin}`);
    const tab = await chrome.tabs.create({
      url: origin.endsWith('/') ? origin : origin + '/',
      active: false,
      pinned: true
    });

    // Wait for tab to land on our origin (SSO may redirect)
    const tabId = tab.id;
    const deadline = Date.now() + POOL_TIMEOUT;
    let onOrigin = false;

    while (Date.now() < deadline) {
      await sleep(800);
      try {
        const current = await chrome.tabs.get(tabId);
        if (!current || !current.url) continue;
        if (current.url === 'about:blank') continue;

        console.log(`[CGW] Tab ${tabId} url: ${current.url} status: ${current.status}`);

        const tabOrigin = new URL(current.url).origin;
        if (tabOrigin.toLowerCase() === origin.toLowerCase() && current.status === 'complete') {
          onOrigin = true;
          break;
        }
      } catch {
        break;
      }
    }

    if (!onOrigin) {
      try { await chrome.tabs.remove(tabId); } catch {}
      throw new Error(`Origin tab timeout: ${origin}`);
    }

    // Wait for auth header (captured by interceptor or webRequest)
    await waitForAuth(origin, AUTH_WAIT_TIMEOUT, tabId);

    originPool.set(origin, { tabId });
    console.log(`[CGW] Origin tab ready: ${origin} (tab ${tabId}, auth: ${authCache.has(origin) ? 'yes' : 'cookies only'})`);
    return tabId;
  });
}

// Wait for auth header to appear in cache (polls both webRequest and interceptor)
async function waitForAuth(origin, timeout, tabId) {
  console.log(`[CGW] Waiting for auth header for ${origin}...`);
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    // Check webRequest capture
    if (authCache.has(origin)) {
      console.log(`[CGW] Auth header captured for ${origin} (webRequest)`);
      return true;
    }
    // Check interceptor capture (read from tab's window.__cgw_auth)
    if (tabId) {
      const auth = await readCapturedAuth(tabId, origin);
      if (auth) {
        console.log(`[CGW] Auth header captured for ${origin} (interceptor)`);
        return true;
      }
    }
    await sleep(500);
  }
  console.log(`[CGW] No auth header for ${origin} (cookies only)`);
  return false;
}

// Refresh auth by reloading the origin tab (mirrors CDP RefreshSessionAuth)
async function refreshOriginAuth(origin) {
  const entry = originPool.get(origin);
  if (!entry) return false;

  console.log(`[CGW] Refreshing auth for ${origin} (reload tab ${entry.tabId})...`);

  // Save old auth header for fallback (mirrors CDP: restore if refresh fails)
  const oldAuth = authCache.get(origin) || null;

  // Clear old auth header
  authCache.delete(origin);

  // Clear interceptor state in the tab
  try {
    await chrome.scripting.executeScript({
      target: { tabId: entry.tabId },
      world: 'MAIN',
      func: () => { window.__cgw_auth = null; }
    });
  } catch {}

  // Reload the tab (like CDP Page.reload)
  try {
    await chrome.tabs.reload(entry.tabId);
  } catch {
    if (oldAuth) authCache.set(origin, oldAuth);
    originPool.delete(origin);
    return false;
  }

  // Wait for tab to finish loading
  const loadDeadline = Date.now() + POOL_TIMEOUT;
  while (Date.now() < loadDeadline) {
    await sleep(800);
    try {
      const current = await chrome.tabs.get(entry.tabId);
      if (current?.status === 'complete') break;
    } catch {
      if (oldAuth) authCache.set(origin, oldAuth);
      originPool.delete(origin);
      return false;
    }
  }

  // Wait for new auth header (up to 15s, matches CDP RefreshSessionAuth)
  const refreshed = await waitForAuth(origin, AUTH_REFRESH_TIMEOUT, entry.tabId);

  // If refresh failed, restore old auth (mirrors CDP behavior)
  if (!refreshed && oldAuth) {
    console.log(`[CGW] Auth refresh failed, restoring previous auth header`);
    authCache.set(origin, oldAuth);
  }

  return refreshed;
}

// Evict a tab from pool (on network error, for fresh session)
// Also unregisters the auth interceptor for this origin
async function evictOriginTab(origin) {
  const entry = originPool.get(origin);
  if (entry) {
    originPool.delete(origin);
    try { await chrome.tabs.remove(entry.tabId); } catch {}
  }
  await unregisterAuthInterceptor(origin);
}

// ── Execute fetch in origin tab context ────────────────────
// Mirrors CDP Runtime.evaluate — runs fetch() in MAIN world only

async function executeFetchInTab(tabId, url, options) {
  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId },
      world: 'MAIN',
      func: async (fetchUrl, fetchOpts) => {
        try {
          const resp = await fetch(fetchUrl, fetchOpts);
          const text = await resp.text();
          return {
            status: resp.status,
            body: text,
            contentType: resp.headers.get('content-type') || ''
          };
        } catch (e) {
          return { status: 0, body: e.message, contentType: 'error' };
        }
      },
      args: [url, options]
    });

    if (results?.[0]?.result) {
      console.log(`[CGW] Fetch executed in MAIN world, status: ${results[0].result.status}`);
      return results[0].result;
    }
  } catch (err) {
    console.log(`[CGW] executeScript MAIN failed: ${err.message}`);
  }

  // Fallback: fetch from Service Worker with cookies from chrome.cookies API
  console.log('[CGW] Fallback: fetch from SW with manual cookies');
  return await fetchWithCookies(url, options);
}

async function fetchWithCookies(url, options) {
  try {
    const urlObj = new URL(url);
    const cookies = await chrome.cookies.getAll({ domain: urlObj.hostname });
    const cookieHeader = cookies.map(c => c.name + '=' + c.value).join('; ');

    const headers = { ...(options.headers || {}) };
    if (cookieHeader) headers['Cookie'] = cookieHeader;

    const resp = await fetch(url, { ...options, headers, credentials: 'omit' });
    const text = await resp.text();
    return {
      status: resp.status,
      body: text,
      contentType: resp.headers.get('content-type') || ''
    };
  } catch (e) {
    return { status: 0, body: e.message, contentType: 'error' };
  }
}

// ── Build fetch options with fresh auth ──────────────────
// Mirrors CDP BuildFetchJs: auth first, then skill headers overlay,
// then credentials mode based on cross-origin + hasAuth

function buildFetchOpts(skill, url, origin, headers, body) {
  const fetchHeaders = {};

  // First: captured auth header (mirrors CDP BuildFetchJs line order)
  const auth = getAuthHeader(origin);
  if (auth) {
    fetchHeaders['Authorization'] = auth;
  }

  // Second: skill-configured headers override captured auth
  for (const [k, v] of Object.entries(headers)) {
    fetchHeaders[k] = v;
  }

  // Add Content-Type for body requests
  if (body && !Object.keys(fetchHeaders).some(k => k.toLowerCase() === 'content-type')) {
    fetchHeaders['Content-Type'] = 'application/json';
  }

  // Credentials mode — matches CDP BuildFetchJs exactly:
  //   cross-origin → 'same-origin' (no cookies, avoid CORS conflict)
  //   same-origin + has auth → 'same-origin' (no cookies, rely on Authorization)
  //   same-origin + no auth → 'include' (send cookies)
  const isCrossOrigin = skill.fetchOrigin && new URL(url).origin !== origin;
  const hasAuth = !!auth;
  const credentials = (isCrossOrigin || hasAuth) ? 'same-origin' : 'include';

  const opts = {
    method: skill.httpMethod,
    headers: fetchHeaders,
    credentials
  };
  if (body) opts.body = body;
  return opts;
}

// ── Invoke a skill ─────────────────────────────────────────

export async function invokeSkill(skillName, params = {}) {
  const skills = await getEnabledSkills();
  const skill = skills.find(s => s.name.toLowerCase() === skillName.toLowerCase());
  if (!skill) throw new Error(`Skill not found: ${skillName}`);

  // Validate required params
  for (const p of skill.parameters || []) {
    if (p.required && (!params[p.name] || !params[p.name].trim())) {
      throw new Error(`Missing required parameter: ${p.name}`);
    }
  }

  // Build URL with path param substitution
  let url = skill.url;
  const remaining = { ...params };
  for (const key of Object.keys(remaining)) {
    const regex = new RegExp(`\\{${escapeRegex(key)}\\}`, 'gi');
    if (regex.test(url)) {
      url = url.replace(regex, encodeURIComponent(remaining[key]));
      delete remaining[key];
    }
  }

  // SSRF protection: validate final URL
  validateUrl(url);
  if (skill.fetchOrigin) validateUrl(skill.fetchOrigin);

  const paramDefs = {};
  for (const p of skill.parameters || []) {
    paramDefs[p.name.toLowerCase()] = p;
  }

  // Build body (POST/PUT/PATCH)
  let body = null;
  if (['POST', 'PUT', 'PATCH'].includes(skill.httpMethod)) {
    if (skill.bodyTemplate) {
      body = substituteTemplate(skill.bodyTemplate, remaining, paramDefs);
    } else {
      body = JSON.stringify(remaining);
    }
  }

  // Build query string (GET/DELETE)
  if (['GET', 'DELETE'].includes(skill.httpMethod) && Object.keys(remaining).length > 0) {
    const qs = Object.entries(remaining)
      .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
      .join('&');
    url += (url.includes('?') ? '&' : '?') + qs;
  }

  // Build skill-configured headers (with template substitution)
  const skillHeaders = {};
  for (const [k, v] of Object.entries(skill.headers || {})) {
    skillHeaders[k] = v.includes('{{') ? substituteTemplate(v, remaining, paramDefs, true) : v;
  }

  // Determine origin for tab pool
  const origin = skill.fetchOrigin || new URL(url).origin;

  // Get or create origin tab
  let tabId = await getOrCreateOriginTab(origin);

  // Read fresh auth right before building opts (from interceptor in tab)
  await readCapturedAuth(tabId, origin);

  // Build fetch options with fresh auth (mirrors CDP BuildFetchJs)
  let fetchOpts = buildFetchOpts(skill, url, origin, skillHeaders, body);

  // Execute fetch in tab context
  let result = await executeFetchInTab(tabId, url, fetchOpts);

  // On 401 — reload page, wait for fresh auth, retry (mirrors CDP RefreshSessionAuth)
  if (result.status === 401) {
    console.log(`[CGW] 401 for ${skillName}, refreshing auth via page reload...`);
    const refreshed = await refreshOriginAuth(origin);
    console.log(`[CGW] Auth refresh ${refreshed ? 'succeeded' : 'failed, using previous auth'}`);

    tabId = await getOrCreateOriginTab(origin);

    // Read fresh auth from reloaded tab
    await readCapturedAuth(tabId, origin);

    fetchOpts = buildFetchOpts(skill, url, origin, skillHeaders, body);
    result = await executeFetchInTab(tabId, url, fetchOpts);

    // Still 401 after refresh — session expired, notify user
    if (result.status === 401) {
      try {
        chrome.notifications.create(`cgw-auth-${origin}`, {
          type: 'basic',
          iconUrl: chrome.runtime.getURL('icons/icon128.png'),
          title: chrome.i18n.getMessage('notifAuthTitle'),
          message: chrome.i18n.getMessage('notifAuthMessage', [origin]),
          priority: 2
        });
      } catch {}
    }
  }

  // On fetch error — evict, create fresh session, retry (mirrors CDP error handling)
  if (result.contentType === 'error' && result.status === 0) {
    console.log(`[CGW] Fetch error for ${skillName}: ${result.body}, evicting and retrying...`);
    await evictOriginTab(origin);
    tabId = await getOrCreateOriginTab(origin);

    await readCapturedAuth(tabId, origin);
    fetchOpts = buildFetchOpts(skill, url, origin, skillHeaders, body);
    result = await executeFetchInTab(tabId, url, fetchOpts);
  }

  // Apply response filter
  let respBody = result.body;
  if (skill.responseFilter && result.status >= 200 && result.status < 300) {
    try {
      respBody = applyFilter(respBody, skill.responseFilter);
    } catch { /* filter failed, return full response */ }
  }

  // Validate status
  const statusCode = result.status;
  if (statusCode < 100 || statusCode > 599) {
    throw new Error(`Invalid status code: ${statusCode}`);
  }

  // Parse response
  if (!respBody || !respBody.trim()) return {};

  let parsed;
  try { parsed = JSON.parse(respBody); }
  catch { parsed = { raw: respBody }; }

  if (statusCode >= 400) {
    throw new Error(`HTTP ${statusCode}: ${JSON.stringify(parsed)}`);
  }

  return parsed;
}

// ── Template substitution ──────────────────────────────────

function substituteTemplate(template, params, paramDefs, isHeader = false) {
  let result = template;
  for (const [key, value] of Object.entries(params)) {
    const placeholder = `{{${key}}}`;
    if (!result.includes(placeholder)) continue;

    const def = paramDefs[key.toLowerCase()];
    const type = def?.type || 'String';
    let substitution;

    switch (type) {
      case 'Integer': {
        const n = parseInt(value, 10);
        if (!Number.isFinite(n)) throw new Error(`Invalid integer for ${key}: ${value}`);
        substitution = String(n);
        break;
      }
      case 'Float': {
        const f = parseFloat(value);
        if (!Number.isFinite(f)) throw new Error(`Invalid float for ${key}: ${value}`);
        substitution = String(f);
        break;
      }
      case 'Boolean':
        substitution = ['true', '1', 'yes'].includes(value.trim().toLowerCase()) ? 'true' : 'false';
        break;
      default:
        if (isHeader) {
          // Block CRLF injection in headers
          if (/[\r\n]/.test(value)) throw new Error(`Invalid header value for ${key}: contains newline`);
          substitution = value;
        } else {
          // JSON body: use full JSON.stringify to properly escape all special chars
          // The placeholder in template should be inside quotes: "field": "{{param}}"
          // JSON.stringify escapes \, ", control chars, unicode correctly
          substitution = JSON.stringify(value).slice(1, -1);
        }
    }
    result = result.replaceAll(placeholder, substitution);
  }

  // For body templates: verify the result is still valid JSON
  if (!isHeader && result.trim()) {
    try { JSON.parse(result); } catch {
      throw new Error('Template substitution produced invalid JSON — check parameter values');
    }
  }

  return result;
}

// ── Response filter ────────────────────────────────────────

function applyFilter(jsonStr, filter) {
  const data = JSON.parse(jsonStr);
  const paths = filter.split(',').map(p => p.trim()).filter(Boolean);
  if (paths.length === 0) return jsonStr;

  const result = Array.isArray(data) ? [] : {};
  for (const path of paths) {
    const parts = path.split('.');
    setNested(result, parts, getNested(data, parts));
  }
  return JSON.stringify(result);
}

function getNested(obj, parts) {
  let current = obj;
  for (let i = 0; i < parts.length; i++) {
    if (current == null) return undefined;
    if (Array.isArray(current)) {
      return current.map(item => getNested(item, parts.slice(i)));
    }
    current = current[parts[i]];
  }
  return current;
}

function setNested(target, parts, value) {
  if (value === undefined) return;
  let current = target;
  for (let i = 0; i < parts.length - 1; i++) {
    if (!(parts[i] in current)) current[parts[i]] = {};
    current = current[parts[i]];
  }
  current[parts[parts.length - 1]] = value;
}

function escapeRegex(str) {
  return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// ── SSRF protection ──────────────────────────────────────────

const PRIVATE_IP_PATTERNS = [
  /^127\./,                           // loopback
  /^10\./,                            // 10.0.0.0/8
  /^172\.(1[6-9]|2\d|3[01])\./,      // 172.16.0.0/12
  /^192\.168\./,                      // 192.168.0.0/16
  /^169\.254\./,                      // link-local
  /^0\./,                             // 0.0.0.0/8
  /^::1$/,                            // IPv6 loopback
  /^fc/i, /^fd/i,                     // IPv6 ULA
  /^fe80/i,                           // IPv6 link-local
];

function validateUrl(urlString) {
  let parsed;
  try {
    parsed = new URL(urlString);
  } catch {
    throw new Error(`Invalid URL: ${urlString}`);
  }

  if (!['http:', 'https:'].includes(parsed.protocol)) {
    throw new Error(`Blocked protocol: ${parsed.protocol} — only http/https allowed`);
  }

  const hostname = parsed.hostname.toLowerCase();

  // Block localhost / loopback
  if (hostname === 'localhost' || hostname === '::1') {
    throw new Error(`Blocked URL: access to ${hostname} is not allowed`);
  }

  // Block private/reserved IP ranges
  for (const pattern of PRIVATE_IP_PATTERNS) {
    if (pattern.test(hostname)) {
      throw new Error(`Blocked URL: access to private IP ${hostname} is not allowed`);
    }
  }
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
