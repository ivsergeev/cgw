// CorpGateway Extension — Skill executor with origin tab pool
//
// Analogous to ChromeCdpService in tray app:
// - Maintains a pool of background tabs (one per origin)
// - Executes fetch() in the context of the origin tab (has cookies/session)
// - Captures Authorization headers via webRequest
// - Handles SSO redirects, token refresh on 401

import { getEnabledSkills } from './storage.js';

// ── Auth header cache (populated by webRequest in background.js) ──

const authCache = new Map(); // origin → Authorization header

export function setAuthHeader(origin, value) {
  authCache.set(origin, value);
}

export function getAuthHeader(origin) {
  return authCache.get(origin) || null;
}

// ── Origin tab pool ────────────────────────────────────────

const originPool = new Map(); // origin → { tabId, ready }
const POOL_TIMEOUT = 30000;   // 30s to wait for tab to load on origin

async function getOrCreateOriginTab(origin) {
  // Check existing tab
  const existing = originPool.get(origin);
  if (existing) {
    try {
      const tab = await chrome.tabs.get(existing.tabId);
      if (tab && !tab.url?.startsWith('chrome://')) {
        return existing.tabId;
      }
    } catch {
      // Tab was closed externally
      originPool.delete(origin);
    }
  }

  // Create new background tab
  console.log(`[CGW] Opening origin tab: ${origin}`);
  const tab = await chrome.tabs.create({
    url: origin + '/',
    active: false,
    pinned: true
  });

  // Wait for tab to land on our origin (SSO may redirect)
  const tabId = tab.id;
  const deadline = Date.now() + POOL_TIMEOUT;

  while (Date.now() < deadline) {
    await sleep(800);
    try {
      const current = await chrome.tabs.get(tabId);
      if (!current || !current.url) continue;
      if (current.url === 'about:blank') continue;

      const tabOrigin = new URL(current.url).origin;
      if (tabOrigin.toLowerCase() === origin.toLowerCase()) {
        // Wait for page to finish loading
        if (current.status === 'complete') {
          originPool.set(origin, { tabId });
          console.log(`[CGW] Origin tab ready: ${origin} (tab ${tabId})`);
          return tabId;
        }
      }
    } catch {
      break; // Tab gone
    }
  }

  // Timeout — cleanup
  try { await chrome.tabs.remove(tabId); } catch {}
  throw new Error(`Origin tab timeout: ${origin} (SSO redirect not completed in ${POOL_TIMEOUT / 1000}s)`);
}

// Evict a tab from pool (on error, for retry)
async function evictOriginTab(origin) {
  const entry = originPool.get(origin);
  if (entry) {
    originPool.delete(origin);
    try { await chrome.tabs.remove(entry.tabId); } catch {}
  }
}

// ── Execute fetch in origin tab context ────────────────────

async function executeFetchInTab(tabId, url, options) {
  // Inject and execute fetch() in the tab's page context
  // This ensures cookies and session are attached automatically
  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN', // page context (has cookies, session)
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

  if (!results || !results[0] || results[0].error) {
    throw new Error(results?.[0]?.error?.message || 'Script execution failed');
  }

  return results[0].result;
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

  // Build headers with template substitution
  const headers = {};
  for (const [k, v] of Object.entries(skill.headers || {})) {
    headers[k] = v.includes('{{') ? substituteTemplate(v, remaining, paramDefs, true) : v;
  }

  // Add Content-Type for body requests
  if (body && !Object.keys(headers).some(k => k.toLowerCase() === 'content-type')) {
    headers['Content-Type'] = 'application/json';
  }

  // Determine origin for tab pool
  const origin = skill.fetchOrigin || new URL(url).origin;

  // Add captured auth header if available
  const auth = getAuthHeader(origin);
  if (auth && !headers['Authorization']) {
    headers['Authorization'] = auth;
  }

  // Get or create origin tab
  const tabId = await getOrCreateOriginTab(origin);

  // Build fetch options
  const fetchOpts = {
    method: skill.httpMethod,
    headers,
    credentials: 'include' // same-origin in tab context — cookies sent
  };
  if (body) fetchOpts.body = body;

  // Execute fetch in tab context
  let result = await executeFetchInTab(tabId, url, fetchOpts);

  // On 401 — evict tab and retry once (session may have expired)
  if (result.status === 401) {
    console.log(`[CGW] 401 for ${skillName}, refreshing origin tab...`);
    await evictOriginTab(origin);
    const newTabId = await getOrCreateOriginTab(origin);

    // Wait a moment for SPA to authenticate
    await sleep(2000);

    result = await executeFetchInTab(newTabId, url, fetchOpts);
  }

  // On fetch error — evict and retry once
  if (result.contentType === 'error' && result.status === 0) {
    console.log(`[CGW] Fetch error for ${skillName}: ${result.body}, retrying...`);
    await evictOriginTab(origin);
    const newTabId = await getOrCreateOriginTab(origin);
    await sleep(1000);
    result = await executeFetchInTab(newTabId, url, fetchOpts);
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
      case 'Integer':
        substitution = String(parseInt(value, 10) || 0);
        break;
      case 'Float':
        substitution = String(parseFloat(value) || 0);
        break;
      case 'Boolean':
        substitution = ['true', '1', 'yes'].includes(value.trim().toLowerCase()) ? 'true' : 'false';
        break;
      default:
        substitution = isHeader ? value : JSON.stringify(value).slice(1, -1);
    }
    result = result.replaceAll(placeholder, substitution);
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

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
