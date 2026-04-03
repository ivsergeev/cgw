# CorpGateway Extension + MCP Server

[Русская версия](docs/README.ru.md)

Chrome extension + MCP server for connecting AI agents to corporate systems via browser session.

> **[Installation Guide](SETUP.md)** | **[Creating Custom Skills](SKILLS.md)** | **[Security](SECURITY.md)**

![CorpGateway Extension](docs/images/CorpGatewayExtension.png)

## How It Works

```
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  AI Agent (OpenCode, Cursor, Claude Code)                    │
│       │                                                      │
│       │ POST /mcp (Bearer token)                             │
│       ▼                                                      │
│  ┌──────────┐    WebSocket     ┌───────────────────────┐     │
│  │ cgw_mcp  │◄────────────────►│  Chrome Extension     │     │
│  │ :9877    │                  │                       │     │
│  │          │                  │  • chrome.cookies     │     │
│  │ Tokens:  │                  │  • chrome.webRequest  │     │
│  │ • agent  │                  │  • fetch() w/ session │     │
│  │ • ext    │                  │  • Skill management   │     │
│  └──────────┘                  └───────────┬───────────┘     │
│                                            │                 │
│                                            │ fetch() + cookies/auth
│                                            ▼                 │
│                                   Corporate APIs             │
│                                   (Jira, Mattermost, ...)    │
└──────────────────────────────────────────────────────────────┘
```

**Key advantage:** the extension reuses your existing browser session for authorization. No need to store passwords, API keys, or configure OAuth — if you're logged into a corporate system in Chrome, the extension automatically uses that session.

## Components

| Component | Description | Location |
|-----------|-------------|----------|
| **Chrome Extension** | Skill management, request execution via browser session | `extension/` |
| **cgw_mcp** | MCP server (HTTP + WebSocket daemon), bridge between agent and extension | `cgw_mcp/` |
| **Presets** | Ready-made skill sets for popular systems | `presets/` |

## Quick Start

### 1. Install the extension

1. Open `chrome://extensions`
2. Enable **Developer mode**
3. Click **Load unpacked** → select the `extension/` folder

### 2. Install and run cgw_mcp

```bash
cd cgw_mcp
npm install
```

**As a daemon (autostart):**

```bash
# Linux / macOS
./install.sh

# Windows (PowerShell)
.\install.ps1
```

**Manually:**

```bash
node index.js
```

On first run, a config file is created at `~/.corpgateway/cgw_mcp.json`:

```json
{
  "port": 9877,
  "token": "abc123...",
  "extensionToken": "def456...",
  "mcpInstructions": "..."
}
```

### 3. Connect the extension to cgw_mcp

1. Extension icon → **⚙ Settings**
2. In the **MCP Connection** section:
   - **Instance name:** anything (e.g. "Chrome Work")
   - **MCP server URL:** `http://localhost:9877`
   - **Extension token:** copy `extensionToken` from `~/.corpgateway/cgw_mcp.json`
3. Click **Save**
4. In the extension popup, click **⚡ Connect**
5. The extension icon turns colored — connection established

### 4. Import skills

1. Extension settings → **Import**
2. Select a file from `presets/` (e.g. `jira.json`)
3. Replace URL placeholders with your actual system addresses

### 5. Connect the AI agent

![OpenCode + CorpGateway](docs/images/CorpGatewayOpencode.png)

Add to your agent config (`opencode.json`, `.cursor/mcp.json`, etc.):

```json
{
  "mcp": {
    "corp": {
      "type": "remote",
      "url": "http://localhost:9877/mcp",
      "headers": {
        "Authorization": "Bearer <token from cgw_mcp.json>"
      }
    }
  }
}
```

## MCP Tools

The agent receives 5 meta-tools:

| Tool | Description | Parameters |
|------|-------------|------------|
| `cgw_groups` | List available groups | — |
| `cgw_list` | List skills (all or by group) | `group?` |
| `cgw_schema` | Get parameter details for a skill (includes `confirm` flag) | `skill` |
| `cgw_invoke` | Invoke a skill (for skills with `confirm=false`) | `skill`, `params?` |
| `cgw_invoke_confirmed` | Invoke a skill that requires confirmation (`confirm=true`) | `skill`, `params?` |

### Agent workflow

```
1. cgw_groups              → sees groups (Jira, Mattermost, ...)
2. cgw_list(group=jira)    → sees skills in group
3. cgw_schema(skill=jira_issue) → sees parameters + confirm flag + which invoke to use
4. cgw_invoke(skill=jira_issue, params={key:"PROJ-1"}) → result (confirm=false)
   cgw_invoke_confirmed(skill=delete_issue, params={key:"PROJ-1"}) → result (confirm=true)
```

## Skill Configuration

> **Detailed guide:** [Creating Custom Skills](SKILLS.md) ([Русский](docs/SKILLS.ru.md))

![Skill Editor](docs/images/CorpGatewayExtensionSettings.png)

### Via extension UI

Extension settings (`chrome://extensions` → CorpGateway → Details → Extension options):

- **Sidebar:** groups with enable/disable toggles
- **Center:** skill list with search, method badges
- **Right panel:** skill editor, test, settings

### Skill structure

| Field | Description |
|-------|-------------|
| Name | Function name for the agent (e.g. `jira_issue`) |
| Description | What the skill does (visible to agent) |
| URL | API endpoint with path parameters `{id}` |
| HTTP method | GET, POST, PUT, PATCH, DELETE |
| Origin URL | Where to execute the request from (if different from URL) |
| Body Template | JSON body template with `{{param}}` placeholders |
| Response Filter | Dot-notation paths for response filtering |
| HTTP headers | Custom headers (support `{{param}}`) |
| Parameters | Name, type (String/Integer/Float/Boolean/Date), required, description |

### Parameter substitution

| Location | Format | Example |
|----------|--------|---------|
| URL path | `{param}` | `/api/issues/{key}` → `/api/issues/PROJ-1` |
| Body | `{{param}}` | `{"text":"{{msg}}"}` → `{"text":"Hello"}` |
| Headers | `{{param}}` | `X-Data: {{token}}` → `X-Data: abc123` |
| Query string | automatic | GET + remaining params → `?q=test&limit=10` |

### Groups

Skills are organized into groups. Each group can be enabled/disabled — disabled groups and their skills are hidden from the agent.

### Presets

Ready-made skill sets in `presets/`:

| File | System | Skills |
|------|--------|--------|
| `jira.json` | Jira REST API | issue, search, comments, transitions |
| `confluence.json` | Confluence REST API | pages, search, spaces |
| `gitlab.json` | GitLab API | projects, issues, merge requests |
| `mattermost.json` | Mattermost API | channels, posts, users, search |
| `outlook.json` | Microsoft Graph API | mail, calendar, contacts |

After import, replace URL placeholders (`JIRA_URL`, `MATTERMOST_URL`, etc.) with actual addresses.

## Authorization

### How the extension accesses APIs

1. **Cookies:** `chrome.cookies` API — the extension has access to cookies for all permitted domains
2. **Authorization headers:** `chrome.webRequest.onSendHeaders` — intercepts Authorization from all browser requests (Bearer tokens, JWT)
3. **Fetch with credentials:** requests are executed from the extension's Service Worker with `credentials: 'include'`

**You don't need to enter passwords or API keys.** If you're logged into a corporate system in Chrome — the extension automatically uses that session.

### Security

```
┌─────────────────────────────────────────────────────────┐
│  Layer 1: HTTP API (cgw_mcp)                            │
│  • Bearer token on every agent request                  │
│  • Timing-safe token comparison                         │
│  • Rate limiting: 10 attempts/min per IP                │
│  • localhost only — not accessible from network         │
│  • CORS restricted to localhost                         │
│  • JSON-RPC method whitelist                            │
│  • Message size limit (1 MB)                            │
│                                                         │
│  Layer 2: WebSocket (cgw_mcp ↔ extension)               │
│  • Extension token for authentication                   │
│  • Mutual auth: HMAC challenge-response                 │
│  • Both sides prove knowledge of extensionToken         │
│                                                         │
│  Layer 3: Chrome Extension                              │
│  • Sender validation — web pages cannot send            │
│    commands to the extension                            │
│  • SSRF protection: private IP blocking, http(s) only   │
│  • Template validation: JSON/HTTP injection protection  │
│  • MCP connection — only via user button click          │
│  • Notifications on auth session expiry                 │
│                                                         │
│  Layer 4: Data                                          │
│  • Credentials not stored — uses Chrome session         │
│  • Tokens in cgw_mcp.json with 0600 permissions         │
│  • Skills in chrome.storage.local (encrypted by Chrome) │
│  • Audit log: last 100 invocations in session storage   │
│  • Tokens masked in logs                                │
│  • Confirmation modes: native (agent permissions) or OTP │
└─────────────────────────────────────────────────────────┘
```

## Configuration

### cgw_mcp.json

Location: `~/.corpgateway/cgw_mcp.json`

```json
{
  "port": 9877,
  "token": "agent-token",
  "extensionToken": "extension-ws-token",
  "mcpInstructions": "Instructions for the agent..."
}
```

| Field | Description |
|-------|-------------|
| `port` | HTTP port for cgw_mcp (default 9877) |
| `token` | Bearer token for the agent |
| `extensionToken` | Token for WebSocket connection from extension |
| `mcpInstructions` | Instruction text sent to agent on MCP initialize |

### Extension (Settings)

| Field | Description |
|-------|-------------|
| Instance name | Identification when using multiple browsers |
| MCP server URL | HTTP address of cgw_mcp |
| Extension token | `extensionToken` from cgw_mcp.json |

## Managing cgw_mcp

### Install as daemon

**Windows:**
```powershell
cd cgw_mcp
.\install.ps1              # install (Task Scheduler)
.\install.ps1 -Uninstall   # remove
```

**Linux:**
```bash
cd cgw_mcp
./install.sh               # install (systemd user service)
./install.sh uninstall      # remove

systemctl --user status cgw-mcp
systemctl --user restart cgw-mcp
journalctl --user -u cgw-mcp -f
```

**macOS:**
```bash
cd cgw_mcp
./install.sh               # install (LaunchAgent)
./install.sh uninstall      # remove

launchctl list | grep cgw-mcp
tail -f ~/.corpgateway/logs/cgw_mcp_launchd.log
```

### Logs

Location: `~/.corpgateway/logs/`

Rotation: last 7 days are kept. Format:

```
[2026-04-01T10:30:15.123Z] INFO cgw_mcp started on http://localhost:9877
[2026-04-01T10:30:20.456Z] INFO Extension connected: "Chrome Work"
[2026-04-01T10:31:05.789Z] INFO → Agent request id=1 method=tools/call
```

## API Reference

### POST /mcp

MCP JSON-RPC endpoint (Streamable HTTP). Requires `Authorization: Bearer <token>`.

### GET /mcp

SSE keep-alive (MCP spec). Requires `Authorization: Bearer <token>`.

### GET /health

```json
{
  "status": "ok",
  "extension": true,
  "extensionName": "Chrome Work"
}
```

### WS /extension/ws

WebSocket for the extension. Two-stage authentication:

1. Connect with `?token=<extensionToken>&name=<instanceName>`
2. Mutual auth (HMAC challenge-response) — both sides prove knowledge of `extensionToken`

## Multiple Browsers

If the extension is installed in multiple Chrome profiles:

1. Each profile has its own extension instance with its own skills
2. Only one can be connected to cgw_mcp at a time
3. The last one to connect displaces the previous
4. `GET /health` shows the name of the connected instance
5. Set different **Instance names** in settings for identification

## File Structure

```
├── extension/                  # Chrome Extension (Manifest V3)
│   ├── manifest.json           # Permissions, service worker
│   ├── background.js           # WS connection to cgw_mcp, auth capture
│   ├── popup.html/js           # Connect button, groups
│   ├── options.html/js         # Full skill editor (3-column UI)
│   ├── lib/
│   │   ├── storage.js          # CRUD skills/groups in chrome.storage
│   │   ├── executor.js         # Skill execution (fetch + substitution)
│   │   └── mcp.js              # MCP JSON-RPC handler (5 meta-tools)
│   ├── _locales/               # i18n (en, ru)
│   └── icons/                  # Colored + gray icons
│
├── cgw_mcp/                    # MCP Server (Node.js)
│   ├── index.js                # HTTP + WebSocket server
│   ├── package.json
│   ├── install.sh              # Daemon installer (Linux/macOS)
│   └── install.ps1             # Daemon installer (Windows)
│
├── presets/                    # Ready-made skill sets
│   ├── jira.json
│   ├── confluence.json
│   ├── gitlab.json
│   ├── mattermost.json
│   └── outlook.json
│
├── docs/                       # Documentation
│   ├── README.ru.md            # README (Russian)
│   ├── SETUP.ru.md             # Setup guide (Russian)
│   ├── SKILLS.ru.md            # Creating custom skills (Russian)
│   └── SECURITY.ru.md          # Security architecture (Russian)
│
├── README.md                   # This file
├── SETUP.md                    # Installation guide
├── SKILLS.md                   # Creating custom skills
└── SECURITY.md                 # Security architecture
```
