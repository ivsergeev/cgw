#!/usr/bin/env node

// cgw_mcp — CorpGateway MCP Server
//
// Agent  → POST /mcp (Bearer token)     → cgw_mcp
// cgw_mcp ↔ WebSocket /extension/ws     ↔ Chrome Extension
//
// Usage:
//   node index.js              — start as daemon (background)
//   node index.js start        — same as above
//   node index.js stop         — stop the daemon
//   node index.js restart      — restart the daemon
//   node index.js status       — show daemon status
//   node index.js --foreground — run in foreground (for debugging / systemd / launchd)
//
// Logs are written to ~/.corpgateway/logs/ with daily rotation.

const http = require('http');
const { WebSocketServer } = require('ws');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');

// ── Config ─────────────────────────────────────────────────

const CONFIG_DIR = path.join(require('os').homedir(), '.corpgateway');
const CONFIG_PATH = path.join(CONFIG_DIR, 'cgw_mcp.json');
const LOG_DIR = path.join(CONFIG_DIR, 'logs');
const PID_PATH = path.join(CONFIG_DIR, 'cgw_mcp.pid');
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

// ── CLI ────────────────────────────────────────────────────

const args = process.argv.slice(2);
const command = args[0] || 'start';

if (command === '--foreground' || command === '-f' || process.env.CGW_FOREGROUND === '1') {
  // Run server in foreground
  startServer();
} else if (command === 'start') {
  daemonStart();
} else if (command === 'stop') {
  daemonStop();
} else if (command === 'restart') {
  daemonStop();
  setTimeout(daemonStart, 500);
} else if (command === 'status') {
  daemonStatus();
} else {
  console.log('Usage: node index.js [start|stop|restart|status|--foreground]');
  console.log('');
  console.log('  start         Start as background daemon (default)');
  console.log('  stop          Stop the daemon');
  console.log('  restart       Restart the daemon');
  console.log('  status        Show daemon status');
  console.log('  --foreground  Run in foreground (for systemd/launchd/debugging)');
  process.exit(1);
}

// ── Daemon management ──────────────────────────────────────

function daemonStart() {
  // Check if already running
  const existingPid = readPid();
  if (existingPid && isProcessAlive(existingPid)) {
    console.log(`cgw_mcp is already running (PID ${existingPid})`);
    process.exit(0);
  }

  // Ensure config exists before spawning
  loadOrCreateConfig();
  fs.mkdirSync(LOG_DIR, { recursive: true });

  // Spawn detached child
  const out = fs.openSync(path.join(LOG_DIR, 'cgw_mcp_daemon.log'), 'a');
  const child = spawn(process.execPath, [__filename, '--foreground'], {
    cwd: __dirname,
    env: { ...process.env, CGW_FOREGROUND: '1' },
    detached: true,
    stdio: ['ignore', out, out],
    windowsHide: true
  });

  // Write PID
  fs.mkdirSync(CONFIG_DIR, { recursive: true });
  fs.writeFileSync(PID_PATH, String(child.pid));

  child.unref();

  console.log(`cgw_mcp started (PID ${child.pid})`);

  // Wait a moment and verify it's alive
  setTimeout(() => {
    if (isProcessAlive(child.pid)) {
      const config = loadOrCreateConfig();
      console.log(`Listening on http://localhost:${config.port}`);
      console.log('');
      console.log(`Config: ${CONFIG_PATH}`);
      console.log(`Logs:   ${LOG_DIR}`);
      console.log(`PID:    ${PID_PATH}`);
      console.log('');
      console.log(`Agent token:     ${config.token}`);
      console.log(`Extension token: ${config.extensionToken}`);
    } else {
      console.error('Failed to start — check logs:', path.join(LOG_DIR, 'cgw_mcp_daemon.log'));
      process.exit(1);
    }
  }, 1000);
}

function daemonStop() {
  const pid = readPid();
  if (!pid) {
    console.log('cgw_mcp is not running (no PID file)');
    return;
  }

  if (!isProcessAlive(pid)) {
    console.log(`cgw_mcp is not running (stale PID ${pid})`);
    cleanupPid();
    return;
  }

  try {
    process.kill(pid, 'SIGTERM');
    console.log(`cgw_mcp stopped (PID ${pid})`);
  } catch (err) {
    // On Windows SIGTERM might not work, try SIGKILL
    try { process.kill(pid, 'SIGKILL'); } catch {}
    console.log(`cgw_mcp killed (PID ${pid})`);
  }

  cleanupPid();
}

function daemonStatus() {
  const pid = readPid();
  if (!pid) {
    console.log('cgw_mcp is not running');
    process.exit(1);
  }

  if (!isProcessAlive(pid)) {
    console.log(`cgw_mcp is not running (stale PID ${pid})`);
    cleanupPid();
    process.exit(1);
  }

  const config = loadOrCreateConfig();
  console.log(`cgw_mcp is running (PID ${pid})`);
  console.log(`Listening on http://localhost:${config.port}`);

  // Try health check
  const req = http.get(`http://localhost:${config.port}/health`, {
    headers: { 'Authorization': `Bearer ${config.token}` },
    timeout: 2000
  }, (res) => {
    let body = '';
    res.on('data', c => body += c);
    res.on('end', () => {
      try {
        const data = JSON.parse(body);
        console.log(`Extension: ${data.extension ? 'connected' : 'not connected'}${data.extensionName ? ' (' + data.extensionName + ')' : ''}`);
      } catch {}
    });
  });
  req.on('error', () => {
    console.log('(health check failed — port may not be ready yet)');
  });
}

function readPid() {
  try {
    const content = fs.readFileSync(PID_PATH, 'utf8').trim();
    const pid = parseInt(content, 10);
    return isNaN(pid) ? null : pid;
  } catch {
    return null;
  }
}

function cleanupPid() {
  try { fs.unlinkSync(PID_PATH); } catch {}
}

function isProcessAlive(pid) {
  try {
    process.kill(pid, 0); // signal 0 = check existence
    return true;
  } catch {
    return false;
  }
}

// ── Server ─────────────────────────────────────────────────

function startServer() {
  const config = loadOrCreateConfig();

  // Write PID (for foreground mode too, so status works)
  fs.mkdirSync(CONFIG_DIR, { recursive: true });
  fs.writeFileSync(PID_PATH, String(process.pid));

  // Cleanup PID on exit
  const cleanup = () => { cleanupPid(); process.exit(0); };
  process.on('SIGTERM', cleanup);
  process.on('SIGINT', cleanup);

  // ── Rotation Logger ──────────────────────────────────────

  let logStream = null;
  let logDate = '';

  function ensureLogStream() {
    const today = new Date().toISOString().slice(0, 10);
    if (today === logDate && logStream) return;

    if (logStream) { logStream.end(); logStream = null; }

    fs.mkdirSync(LOG_DIR, { recursive: true });
    logDate = today;
    logStream = fs.createWriteStream(path.join(LOG_DIR, `cgw_mcp_${today}.log`), { flags: 'a' });

    try {
      const files = fs.readdirSync(LOG_DIR)
        .filter(f => f.startsWith('cgw_mcp_') && f.endsWith('.log') && !f.includes('daemon'))
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

  // ── State ────────────────────────────────────────────────

  let extWs = null;
  let extName = '';
  const pending = new Map();

  // ── HTTP Server ──────────────────────────────────────────

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

  // ── WebSocket Server (extension) ─────────────────────────

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
      ws.isAlive = true;
      log('INFO', `Extension connected: "${extName}"`);

      ws.on('pong', () => { ws.isAlive = true; });

      ws.on('message', (data) => {
        ws.isAlive = true;
        try {
          const msg = JSON.parse(data);
          // Notifications (no id) — keepalive pings, etc. Just ignore.
          if (msg.id === undefined || msg.id === null) return;
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

  // ── POST /mcp ────────────────────────────────────────────

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

  // ── GET /health ──────────────────────────────────────────

  function handleHealth(req, res) {
    if (req.method === 'OPTIONS') { res.writeHead(200); res.end(); return; }
    if (!checkAuth(req)) return unauthorized(res);
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      status: 'ok',
      pid: process.pid,
      extension: extWs !== null && extWs.readyState === 1,
      extensionName: extName || null
    }));
  }

  // ── Helpers ──────────────────────────────────────────────

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

  // ── WebSocket keepalive ─────────────────────────────────
  // Server-side ping every 30s — detects dead connections
  // (browser crash, network drop without TCP FIN)

  setInterval(() => {
    if (extWs) {
      if (!extWs.isAlive) {
        log('WARN', 'Extension ping timeout, closing connection');
        extWs.terminate();
        return;
      }
      extWs.isAlive = false;
      try { extWs.ping(); } catch {}
    }
  }, 30000);

  // ── Start listener ───────────────────────────────────────

  server.listen(config.port, '127.0.0.1', () => {
    log('INFO', `cgw_mcp started on http://localhost:${config.port} (PID ${process.pid})`);
    log('INFO', `Agent token:     ${config.token}`);
    log('INFO', `Extension token: ${config.extensionToken}`);
    log('INFO', `Config: ${CONFIG_PATH}`);
    log('INFO', `Logs:   ${LOG_DIR}`);
  });
}

// ── Config loader ──────────────────────────────────────────

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
