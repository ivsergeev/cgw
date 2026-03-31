#!/usr/bin/env node

// cgw_mcp — CorpGateway MCP Server
//
// Agent  → POST /mcp (Bearer token)     → cgw_mcp
// cgw_mcp ↔ WebSocket /extension/ws     ↔ Chrome Extension
//
// Logs are written to ~/.corpgateway/logs/ with daily rotation.

const http = require('http');
const { WebSocketServer } = require('ws');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

// ── Config ─────────────────────────────────────────────────

const CONFIG_DIR = path.join(require('os').homedir(), '.corpgateway');
const CONFIG_PATH = path.join(CONFIG_DIR, 'cgw_mcp.json');
const LOG_DIR = path.join(CONFIG_DIR, 'logs');
const MAX_LOG_FILES = 7; // keep last 7 days

const DEFAULT_INSTRUCTIONS = `Corporate API gateway for accessing internal corporate systems.

Workflow:
1. Call cgw_groups to see available groups
2. Call cgw_list (optionally with group filter) to see skills
3. Call cgw_schema if you need parameter details
4. Call cgw_invoke to execute a skill

Rules:
- Only call skills from cgw_list. Never guess skill names.
- Always call cgw_invoke for fresh data; do not cache results.
- Confirm with user before calling write operations.`;

function loadOrCreateConfig() {
  let config = { port: 9877, token: '', extensionToken: '', mcpInstructions: '' };
  try {
    config = { ...config, ...JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8')) };
  } catch {}

  let changed = false;
  if (!config.token) { config.token = crypto.randomBytes(16).toString('hex'); changed = true; }
  if (!config.extensionToken) { config.extensionToken = crypto.randomBytes(16).toString('hex'); changed = true; }
  if (!config.port) { config.port = 9877; changed = true; }
  if (!config.mcpInstructions) { config.mcpInstructions = DEFAULT_INSTRUCTIONS; changed = true; }
  if (changed) {
    fs.mkdirSync(CONFIG_DIR, { recursive: true });
    fs.writeFileSync(CONFIG_PATH, JSON.stringify(config, null, 2));
  }
  return config;
}

const config = loadOrCreateConfig();

// ── Rotation Logger ────────────────────────────────────────

let logStream = null;
let logDate = '';

function getLogPath(date) {
  return path.join(LOG_DIR, `cgw_mcp_${date}.log`);
}

function ensureLogStream() {
  const today = new Date().toISOString().slice(0, 10); // YYYY-MM-DD
  if (today === logDate && logStream) return;

  // Close old stream
  if (logStream) { logStream.end(); logStream = null; }

  // Open new
  fs.mkdirSync(LOG_DIR, { recursive: true });
  logDate = today;
  logStream = fs.createWriteStream(getLogPath(today), { flags: 'a' });

  // Cleanup old logs
  try {
    const files = fs.readdirSync(LOG_DIR)
      .filter(f => f.startsWith('cgw_mcp_') && f.endsWith('.log'))
      .sort();
    while (files.length > MAX_LOG_FILES) {
      const old = files.shift();
      fs.unlinkSync(path.join(LOG_DIR, old));
    }
  } catch {}
}

function log(level, msg, data) {
  const ts = new Date().toISOString();
  const line = data !== undefined
    ? `[${ts}] ${level} ${msg} ${JSON.stringify(data)}`
    : `[${ts}] ${level} ${msg}`;

  console.log(line);
  ensureLogStream();
  logStream.write(line + '\n');
}

// ── State ──────────────────────────────────────────────────

let extWs = null;
let extName = '';  // name of connected extension instance
const pending = new Map();

// ── HTTP Server ────────────────────────────────────────────

const server = http.createServer((req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type');

  if (req.method === 'OPTIONS') { res.writeHead(200); res.end(); return; }

  const url = new URL(req.url, `http://localhost:${config.port}`);

  if (url.pathname === '/mcp') return handleMCP(req, res);
  if (url.pathname === '/health') return handleHealth(req, res);

  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: 'Not found' }));
});

// ── WebSocket Server (extension) ───────────────────────────

const wss = new WebSocketServer({ noServer: true });

server.on('upgrade', (req, socket, head) => {
  const url = new URL(req.url, `http://localhost:${config.port}`);

  if (url.pathname !== '/extension/ws') { socket.destroy(); return; }

  if (url.searchParams.get('token') !== config.extensionToken) {
    log('WARN', 'Extension WS auth failed');
    socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n');
    socket.destroy();
    return;
  }

  wss.handleUpgrade(req, socket, head, (ws) => {
    if (extWs && extWs.readyState === 1) extWs.close();
    extWs = ws;
    extName = url.searchParams.get('name') || 'default';
    log('INFO', `Extension connected: "${extName}"`);

    ws.on('message', (data) => {
      try {
        const msg = JSON.parse(data);
        const key = String(msg.id);
        const p = pending.get(key);
        if (p) {
          clearTimeout(p.timer);
          pending.delete(key);
          p.resolve(data);
          log('INFO', `← Extension response id=${key}`);
        }
      } catch {}
    });

    ws.on('close', () => {
      if (extWs === ws) { extWs = null; extName = ''; }
      log('INFO', 'Extension disconnected');
    });
  });
});

// ── POST /mcp ──────────────────────────────────────────────

function handleMCP(req, res) {
  if (req.method === 'GET') {
    if (!checkAuth(req)) return unauthorized(res);
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', 'Connection': 'keep-alive' });
    req.on('close', () => res.end());
    return;
  }

  if (req.method !== 'POST') {
    res.writeHead(405, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'method not allowed' }));
    return;
  }

  if (!checkAuth(req)) return unauthorized(res);

  readBody(req, (err, body) => {
    if (err) {
      res.writeHead(400, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: 'read error' }));
      return;
    }

    let msg;
    try { msg = JSON.parse(body); } catch {
      res.writeHead(400, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ jsonrpc: '2.0', error: { code: -32700, message: 'Parse error' } }));
      return;
    }

    // Notification (no id) → 202
    if (msg.id === undefined || msg.id === null) {
      res.writeHead(202); res.end();
      return;
    }

    const method = msg.method || '';
    log('INFO', `→ Agent request id=${msg.id} method=${method}`);

    // Handle initialize and ping locally
    if (method === 'initialize') {
      const resp = {
        jsonrpc: '2.0', id: msg.id,
        result: {
          protocolVersion: '2025-03-26',
          capabilities: { tools: {} },
          serverInfo: { name: 'CorpGateway', version: '1.0.0' },
          instructions: config.mcpInstructions
        }
      };
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(resp));
      log('INFO', `← Initialize response`);
      return;
    }
    if (method === 'ping') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ jsonrpc: '2.0', id: msg.id, result: {} }));
      return;
    }

    // Forward to extension
    if (!extWs || extWs.readyState !== 1) {
      log('WARN', 'Extension not connected, rejecting request');
      res.writeHead(502, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ jsonrpc: '2.0', id: msg.id, error: { code: -32000, message: 'Extension not connected' } }));
      return;
    }

    const reqID = String(msg.id);
    const promise = new Promise((resolve) => {
      const timer = setTimeout(() => {
        pending.delete(reqID);
        resolve(null);
      }, 30000);
      pending.set(reqID, { resolve, timer });
    });

    try {
      extWs.send(body);
      log('INFO', `→ Forwarded to extension id=${reqID}`);
    } catch {
      pending.delete(reqID);
      log('ERROR', `Extension send failed id=${reqID}`);
      res.writeHead(502, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ jsonrpc: '2.0', id: msg.id, error: { code: -32000, message: 'Extension send failed' } }));
      return;
    }

    promise.then((response) => {
      if (response === null) {
        log('WARN', `Extension timeout id=${reqID}`);
        res.writeHead(504, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ jsonrpc: '2.0', id: msg.id, error: { code: -32000, message: 'Extension timeout' } }));
      } else {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(response);
      }
    });
  });
}

// ── GET /health ────────────────────────────────────────────

function handleHealth(req, res) {
  if (req.method === 'OPTIONS') { res.writeHead(200); res.end(); return; }
  if (!checkAuth(req)) return unauthorized(res);
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({
    status: 'ok',
    extension: extWs !== null && extWs.readyState === 1,
    extensionName: extName || null
  }));
}

// ── Helpers ────────────────────────────────────────────────

function checkAuth(req) {
  return req.headers.authorization === `Bearer ${config.token}`;
}

function unauthorized(res) {
  res.writeHead(401, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: 'Unauthorized' }));
}

function readBody(req, cb) {
  const chunks = [];
  let size = 0;
  req.on('data', (chunk) => {
    size += chunk.length;
    if (size > 1 << 20) { cb(new Error('too large')); req.destroy(); return; }
    chunks.push(chunk);
  });
  req.on('end', () => cb(null, Buffer.concat(chunks).toString()));
  req.on('error', cb);
}

// ── Start ──────────────────────────────────────────────────

server.listen(config.port, '127.0.0.1', () => {
  log('INFO', `cgw_mcp started on http://localhost:${config.port}`);
  log('INFO', `Agent token:     ${config.token}`);
  log('INFO', `Extension token: ${config.extensionToken}`);
  log('INFO', `Config: ${CONFIG_PATH}`);
  log('INFO', `Logs:   ${LOG_DIR}`);
});
