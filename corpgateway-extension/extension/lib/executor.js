// CorpGateway Extension — Skill executor
// Handles parameter substitution, fetch with cookies/auth, response filtering

import { getEnabledSkills } from './storage.js';

// ── Auth header cache (populated by webRequest listener in background.js) ──
const authCache = new Map(); // origin → Authorization header

export function setAuthHeader(origin, value) {
  authCache.set(origin, value);
}

export function getAuthHeader(origin) {
  return authCache.get(origin) || null;
}

// ── Invoke a skill ─────────────────────────────────────────────

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
    const placeholder = `{${key}}`;
    // Case-insensitive URL placeholder check
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
    url = `${url}?${qs}`;
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

  // Add Authorization from cache if available
  const origin = skill.fetchOrigin || new URL(url).origin;
  const auth = getAuthHeader(origin);
  if (auth && !headers['Authorization']) {
    headers['Authorization'] = auth;
  }

  // Execute fetch
  const fetchOpts = {
    method: skill.httpMethod,
    headers,
    credentials: 'include'
  };
  if (body) fetchOpts.body = body;

  const resp = await fetch(url, fetchOpts);
  let respText = await resp.text();

  // Apply response filter
  if (skill.responseFilter && resp.ok) {
    try {
      respText = applyFilter(respText, skill.responseFilter);
    } catch { /* filter failed, return full response */ }
  }

  const statusCode = resp.status;
  if (statusCode < 100 || statusCode > 599) {
    throw new Error(`Invalid status code: ${statusCode}`);
  }

  // Parse as JSON if possible
  let result;
  if (!respText.trim()) {
    result = {};
  } else {
    try { result = JSON.parse(respText); }
    catch { result = { raw: respText }; }
  }

  if (statusCode >= 400) {
    throw new Error(`HTTP ${statusCode}: ${JSON.stringify(result)}`);
  }

  return result;
}

// ── Template substitution ──────────────────────────────────────

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
      default: // String, Date
        if (isHeader) {
          substitution = value; // Headers: raw value
        } else {
          // JSON body: escape string (remove outer quotes from JSON.stringify)
          substitution = JSON.stringify(value).slice(1, -1);
        }
    }
    result = result.replaceAll(placeholder, substitution);
  }
  return result;
}

// ── Response filter (dot-notation) ─────────────────────────────

function applyFilter(jsonStr, filter) {
  const data = JSON.parse(jsonStr);
  const paths = filter.split(',').map(p => p.trim()).filter(Boolean);
  if (paths.length === 0) return jsonStr;

  const result = Array.isArray(data) ? [] : {};

  for (const path of paths) {
    const parts = path.split('.');
    setNestedValue(result, parts, getNestedValue(data, parts));
  }

  return JSON.stringify(result);
}

function getNestedValue(obj, parts) {
  let current = obj;
  for (const part of parts) {
    if (current == null) return undefined;
    if (Array.isArray(current)) {
      return current.map(item => getNestedValue(item, [part, ...parts.slice(parts.indexOf(part) + 1)]));
    }
    current = current[part];
  }
  return current;
}

function setNestedValue(target, parts, value) {
  if (value === undefined) return;
  let current = target;
  for (let i = 0; i < parts.length - 1; i++) {
    if (!(parts[i] in current)) {
      current[parts[i]] = {};
    }
    current = current[parts[i]];
  }
  current[parts[parts.length - 1]] = value;
}

function escapeRegex(str) {
  return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
