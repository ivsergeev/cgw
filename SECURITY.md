# CorpGateway Security Architecture

[Русская версия](docs/SECURITY.ru.md)

This document describes the security mechanisms implemented in CorpGateway, the threats they protect against, and their limitations.

---

## Overview

CorpGateway has a four-layer security model:

```
┌─────────────────────────────────────────────────────────┐
│  Layer 1: HTTP API (cgw_mcp)                            │
│  Layer 2: WebSocket (cgw_mcp ↔ extension)               │
│  Layer 3: Chrome Extension                              │
│  Layer 4: Data                                          │
└─────────────────────────────────────────────────────────┘
```

---

## Layer 1: HTTP API (cgw_mcp)

### Token Authentication

All HTTP requests to `/mcp` and `/health` require a Bearer token:

```
Authorization: Bearer <token>
```

The token is auto-generated (128-bit hex) on first server start and stored in `~/.corpgateway/cgw_mcp.json`.

### Timing-Safe Comparison

Token comparison uses `crypto.timingSafeEqual()` to prevent timing attacks. When token lengths differ, a dummy comparison is performed to maintain consistent response time.

### Rate Limiting

Failed authentication attempts are tracked per IP address:
- **Limit:** 10 failures per 1-minute window
- **Response:** HTTP 429 (Too Many Requests)
- **Cleanup:** Stale entries removed every 5 minutes

Applies to both HTTP endpoints and WebSocket connections.

### CORS Policy

Only localhost origins are allowed:

```
Access-Control-Allow-Origin: http://localhost[:port] | http://127.0.0.1[:port]
```

Requests from any other origin receive no CORS headers — the browser blocks them.

### Method Whitelist

Only these JSON-RPC methods are forwarded to the extension:
- `initialize`, `ping`, `tools/list`, `tools/call`, `notifications/initialized`

All other methods are rejected with error code `-32601`.

### Message Size Limits

- **HTTP body:** 1 MB max (request destroyed if exceeded)
- **WebSocket:** 1 MB max (`maxPayload` in WebSocketServer)

### Request Timeouts

- **HTTP:** 60-second timeout on request and response
- **Extension response:** 30-second timeout per forwarded request
- **Concurrent limit:** Maximum 100 pending requests

### Error Codes

Structured error codes help agents understand failure reasons without exposing internals:

| Code | Meaning |
|------|---------|
| `-32001` | Extension not connected |
| `-32002` | Request timeout |
| `-32003` | Service overloaded |
| `-32004` | Extension connection lost |

### Localhost Binding

The server binds to `127.0.0.1` only — not accessible from the network.

---

## Layer 2: WebSocket (Mutual Authentication)

The WebSocket connection between cgw_mcp and the extension uses a three-step mutual authentication protocol based on HMAC-SHA256.

### Protocol

```
Extension → Server:  WS connect with ?token=<extensionToken>
Server:              Validates extensionToken (one-way auth)

Server → Extension:  { type: "challenge",
                       challenge: <random 32 bytes>,
                       serverProof: HMAC-SHA256(extensionToken, "server:" + challenge) }
Extension:           Verifies serverProof ← proves server knows extensionToken

Extension → Server:  { type: "challenge-response",
                       hmac: HMAC-SHA256(extensionToken, "client:" + challenge) }
Server:              Verifies hmac ← proves extension knows extensionToken

Server → Extension:  { type: "authenticated" }
```

### What This Prevents

- **Fake server:** An attacker running a rogue WebSocket server on the same port cannot produce a valid `serverProof` without knowing `extensionToken`. The extension verifies the proof and closes the connection if it doesn't match.
- **Fake extension:** An attacker cannot produce a valid `clientProof` without `extensionToken`. The server closes the connection on mismatch.
- **Replay attacks:** Each challenge is a fresh 32-byte random value. The `"server:"` / `"client:"` prefixes prevent using the same HMAC for both roles.

### Timing Protection

- Server uses `crypto.timingSafeEqual()` for HMAC comparison
- Extension uses Web Crypto API (`crypto.subtle`) which provides constant-time operations
- Auth timeout: 5 seconds (connection closed if not completed)

### Message Processing

No messages are processed before mutual authentication completes. The extension tracks state with `serverAuthenticated` and `challengeSent` flags — out-of-sequence messages cause immediate disconnection.

---

## Layer 3: Chrome Extension

### Sender Validation

The extension's message handler validates the sender of every message:

- **`getStatus`** — allowed from any sender (read-only, used by overlay)
- **`connect`, `disconnect`, `mcpRequest`, `configUpdated`, `skillsChanged`** — restricted to extension's own pages (popup, options). Verified by checking `sender.id === chrome.runtime.id` and `sender.url.startsWith(chrome.runtime.getURL(''))`.

Messages from web pages, content scripts on arbitrary sites, or other extensions are rejected.

### SSRF Protection

Before executing any skill, the final URL and `fetchOrigin` are validated:

**Blocked:**
- Non-HTTP protocols (`file:`, `ftp:`, `javascript:`, `data:`, etc.)
- `localhost`, `::1`
- Private IP ranges: `10.x`, `172.16-31.x`, `192.168.x`, `169.254.x`
- IPv6 private: `fc00::/7`, `fd00::/8`, `fe80::/10`
- `0.0.0.0/8`

This prevents skills from being used to scan or access internal networks.

### Template Injection Protection

Parameter substitution in request bodies and headers is sanitized:

- **Headers:** Values containing `\r` or `\n` are rejected (CRLF injection prevention)
- **JSON body:** Values are escaped via `JSON.stringify()`. After substitution, the result is parsed with `JSON.parse()` to verify it's still valid JSON
- **Integers/Floats:** Validated with `Number.isFinite()` (prevents NaN/Infinity injection)
- **Booleans:** Strict whitelist (`true`, `1`, `yes` → true; everything else → false)

### Skill Import Validation

Imported preset files are validated before storage:

- URL protocol must be `http:` or `https:`
- HTTP method must be one of: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`
- Maximum 50 parameters per skill
- Maximum 20 custom headers per skill
- Parameter types must be: `String`, `Integer`, `Float`, `Boolean`, `Date`
- Duplicate skills/groups are skipped (case-insensitive name matching)

### Operation Confirmation (OTP)

Skills marked with `confirm: true` require a one-time confirmation code before execution.

**Flow:**
1. Agent calls `cgw_invoke(skill, params)`
2. Extension generates a 4-digit code (cryptographically random)
3. Code is shown to the user via OS notification
4. Extension returns "confirmation required" to the agent
5. Agent asks user for the code
6. Agent calls `cgw_invoke(skill, params, confirmCode)`
7. Extension validates the code and executes

**Properties:**
- Code is 4 random digits (0000–9999), generated with `crypto.getRandomValues()`
- Valid for 60 seconds, then auto-expires
- One-time use — consumed after successful validation
- Bound to specific skill + parameters (SHA-256 hash of skill name and sorted params)
- Code exists only in the OS notification — the agent cannot see it
- `confirmCode` is excluded from parameter hashing (prevents hash mismatch between first and second call)

**What this prevents:**
- Prompt injection causing unintended write operations
- Agent autonomously executing destructive actions without user awareness

### Auth Failure Notification

When a skill returns 401 after an automatic auth refresh attempt, the extension shows an OS notification asking the user to re-login to the affected system.

---

## Layer 4: Data Protection

### Config File Permissions

`~/.corpgateway/cgw_mcp.json` contains both tokens:

- **Directory:** created with mode `0o700` (owner only)
- **File:** created with mode `0o600` (owner read/write only)
- **Enforcement:** permissions are checked and corrected on every server start (Unix/macOS)

### Token Masking in Logs

Tokens are never written to log files in full. Only the first 4 and last 4 characters are shown:

```
Agent token:     a1b2...f8e7
Extension token: c3d4...h2i1
```

### Audit Log

All skill invocations are recorded in `chrome.storage.session`:

- Last 100 entries
- Each entry: skill name, parameter names (not values), errors, duration, timestamp
- Whether confirmation was required
- Cleared on extension restart (session storage)
- Accessible via `cgw_audit` tool

### Chrome Storage

Skills, groups, and extension config are stored in `chrome.storage.local`, which is encrypted by Chrome and accessible only to the extension.

---

## Security Summary

| Area | Protection |
|------|------------|
| Token authentication | Timing-safe comparison + rate limiting (10/min per IP) |
| Server identity | Mutual HMAC-SHA256 challenge-response on WebSocket |
| Extension identity | Extension token + HMAC verification |
| Cross-origin requests | CORS restricted to localhost + Bearer token required |
| API methods | Whitelist of allowed JSON-RPC methods |
| Message size | 1 MB limit on HTTP and WebSocket |
| Connection stability | 60-second timeouts + max 100 concurrent requests |
| Internal network access | URL validation blocks private IPs and non-HTTP protocols |
| Request injection | CRLF check in headers + JSON validation in body |
| Destructive operations | Dual: `cgw_invoke_confirmed` (native client prompt) + OTP fallback |
| Skill import | Protocol, method, parameter type and count validation |
| Message origin | Sender validation — only extension pages can send commands |
| Config file | Restricted file permissions (0600 on Unix) |
| Log security | Tokens masked to first/last 4 characters |
| Audit | Last 100 invocations recorded with timestamps |
| Network exposure | Server binds to 127.0.0.1 only |

## Recommendations

- Use a **dedicated Chrome profile** for CorpGateway to isolate corporate sessions
- **Enable only needed** skill groups — disable groups when not in use
- **Enable confirmation** on skills that perform write operations
- Keep cgw_mcp and the extension **up to date**
