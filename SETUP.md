# CorpGateway — Installation and Setup Guide

[Русская версия](docs/SETUP.ru.md)

Step-by-step instructions: from a clean Chrome profile to a working AI agent with access to corporate systems.

---

## Contents

1. [Create a dedicated Chrome profile](#1-create-a-dedicated-chrome-profile)
2. [Install the extension](#2-install-the-extension)
3. [Install and run cgw_mcp server](#3-install-and-run-cgw_mcp-server)
4. [Configure the extension](#4-configure-the-extension)
5. [Import skills](#5-import-skills)
6. [Connect to OpenCode](#6-connect-to-opencode)
7. [Verify everything works](#7-verify-everything-works)
8. [Troubleshooting](#8-troubleshooting)

---

## 1. Create a dedicated Chrome profile

It's recommended to use a separate Chrome profile for CorpGateway. This allows you to:
- Keep your main browser unaffected
- Isolate corporate sessions
- Easily switch between profiles

### Steps

1. Open Chrome
2. Click the **profile icon** (top-right corner, next to the three dots)
3. Click **Add**
4. Choose **Continue without an account** (or link a Google account)
5. Enter a profile name, e.g. **"CorpGateway"**
6. Pick a color or icon for visual distinction
7. Click **Done**

A new Chrome window will open with a clean profile.

### Log in to corporate systems

In this profile, log in to all corporate systems you need access to:
- Jira
- Confluence
- GitLab
- Mattermost
- Outlook / Microsoft 365
- Other internal services

> **Important:** the extension uses your existing browser session. If you're not logged into a system — skills for it won't be able to authenticate.

---

## 2. Install the extension

### Requirements

- Chrome 116+ (Manifest V3)

### Steps

1. Navigate to `chrome://extensions`
2. Enable **Developer mode** (toggle in the top-right corner)
3. Click **Load unpacked**
4. Select the `extension/` folder
5. The extension will appear in the list — make sure it's enabled

The CorpGateway icon will appear in the extensions bar (right of the address bar). If the icon is gray — the extension is loaded but not yet connected to the server.

> **Tip:** pin the extension — click the puzzle icon (Extensions) → find CorpGateway → click the pin.

---

## 3. Install and run cgw_mcp server

cgw_mcp is a local MCP server that connects the AI agent to the extension via WebSocket.

### Requirements

- Node.js 18+

### Install dependencies

```bash
cd cgw_mcp
npm install
```

### First run

```bash
node index.js --foreground
```

On first run, a configuration file is automatically created:

```
~/.corpgateway/cgw_mcp.json
```

```json
{
  "port": 9877,
  "token": "a1b2c3d4...",
  "extensionToken": "e5f6g7h8...",
  "mcpInstructions": "..."
}
```

| Field | Description |
|-------|-------------|
| `port` | Server port (default 9877) |
| `token` | Token for the AI agent (Bearer auth) |
| `extensionToken` | Token for WebSocket connection from extension |
| `mcpInstructions` | System instruction for the agent |

> **Note the `token` and `extensionToken` values** — you'll need them in the next steps.

Stop the server (Ctrl+C) and restart as a daemon:

### Run as daemon (background process)

**Windows (PowerShell):**

```powershell
cd cgw_mcp
.\install.ps1
```

Creates a Task Scheduler task that starts cgw_mcp at logon.

**Linux (systemd):**

```bash
cd cgw_mcp
./install.sh
```

**macOS (launchd):**

```bash
cd cgw_mcp
./install.sh
```

### Daemon management

```bash
node index.js status    # check status
node index.js stop      # stop
node index.js start     # start
node index.js restart   # restart
```

### Remove daemon

```powershell
# Windows
.\install.ps1 -Uninstall
```

```bash
# Linux / macOS
./install.sh uninstall
```

---

## 4. Configure the extension

### Open settings

Click the extension icon → **⚙ Settings** (at the bottom of the popup).

Or: `chrome://extensions` → CorpGateway → **Details** → **Extension options**

### Fill in MCP connection

In the settings page, under **"MCP Connection"**:

| Field | Value |
|-------|-------|
| **Instance name** | Any descriptive name, e.g. `Chrome Work` |
| **MCP server URL** | `http://localhost:9877` |
| **Extension token** | `extensionToken` value from `~/.corpgateway/cgw_mcp.json` |

Click **Save**.

### Connect

1. Click the extension icon in the Chrome toolbar
2. Click the big **⚡** button (Connect)
3. Status changes to **"Connected"** (green)
4. The extension icon turns colored
5. A purple border appears around the page — active connection indicator

> If the connection doesn't establish — make sure cgw_mcp server is running (`node index.js status`).

---

## 5. Import skills

Skills define which APIs are available to the AI agent. You can create them manually or import ready-made presets.

### Import presets

1. In extension settings, click **Import** (icon in the top toolbar)
2. Select a file from the `presets/` folder:

| File | System |
|------|--------|
| `jira.json` | Jira REST API |
| `confluence.json` | Confluence REST API |
| `gitlab.json` | GitLab API |
| `mattermost.json` | Mattermost API |
| `outlook.json` | Microsoft Graph API |

3. After import, **replace URL placeholders** with your actual system addresses:
   - Open the imported group
   - In each skill, replace `JIRA_URL`, `MATTERMOST_URL`, etc. with real domains
   - For example: `https://JIRA_URL/rest/api/2/issue/{key}` → `https://jira.mycompany.com/rest/api/2/issue/{key}`

### Enable/disable groups

In the extension popup or settings sidebar, each group has a toggle. Disabled groups and their skills are hidden from the AI agent.

### Operation confirmation

Skills can require a one-time confirmation code before execution. When enabled, the extension shows a 4-digit code via an OS notification — the user tells the code to the agent, and the agent retries the call with the code.

To enable: open a skill in the editor → check **"Require confirmation"**. See [Creating Custom Skills](SKILLS.md#operation-confirmation) for details.

---

## 6. Connect to OpenCode

### Configuration

Create or edit `opencode.json` in your project root:

```json
{
  "mcp": {
    "corp": {
      "type": "remote",
      "url": "http://localhost:9877/mcp",
      "headers": {
        "Authorization": "Bearer <TOKEN>"
      }
    }
  }
}
```

Replace `<TOKEN>` with the `token` value from `~/.corpgateway/cgw_mcp.json`.

> **Note:** `token` is the agent token (not `extensionToken`). These are different tokens.

### Alternative: global configuration

To make CorpGateway available in all projects, add it to the global config:

```
~/.config/opencode/opencode.json
```

```json
{
  "mcp": {
    "corp": {
      "type": "remote",
      "url": "http://localhost:9877/mcp",
      "headers": {
        "Authorization": "Bearer <TOKEN>"
      }
    }
  }
}
```

### Launch OpenCode

```bash
opencode
```

On startup, OpenCode will automatically connect to cgw_mcp and receive the available tools: `cgw_groups`, `cgw_list`, `cgw_schema`, `cgw_invoke`, `cgw_invoke_confirmed`.

To enable native confirmation prompts for skills with `confirm=true`, add to your `opencode.json`:

```json
{
  "permissions": {
    "mcp:corp:cgw_invoke_confirmed": "ask"
  }
}
```

This makes OpenCode ask for approval in the terminal before executing confirmed skills.

---

## 7. Verify everything works

### Checklist

- [ ] cgw_mcp server is running (`node index.js status` → running)
- [ ] Extension is connected (colored icon, purple border)
- [ ] Skills are imported and groups are enabled
- [ ] Browser has active sessions in the required systems
- [ ] OpenCode is configured with the correct token

### Test commands in OpenCode

Ask the agent:

```
Show the list of available groups in CorpGateway
```

The agent will call `cgw_groups` and display the group list.

```
Find issue PROJ-123 in Jira
```

The agent will call `cgw_invoke(skill=jira_issue, params={key: "PROJ-123"})`.

### Verify via HTTP (curl)

```bash
# Server status
curl -H "Authorization: Bearer <TOKEN>" http://localhost:9877/health

# Expected response:
# {"status":"ok","extension":true,"extensionName":"Chrome Work"}
```

---

## 8. Troubleshooting

### Extension won't connect

| Symptom | Solution |
|---------|----------|
| Icon stays gray | Check that cgw_mcp is running: `node index.js status` |
| "No extension token" | Open extension settings and enter `extensionToken` |
| WebSocket error | Check MCP server URL: should be `http://localhost:9877` |

### Agent doesn't see tools

| Symptom | Solution |
|---------|----------|
| No tools in OpenCode | Check `token` (not `extensionToken`) in `opencode.json` |
| Empty group list | Enable at least one group in the extension popup |
| `extension: false` in /health | Click ⚡ Connect in the extension popup |

### Skills return errors

| Symptom | Solution |
|---------|----------|
| 401 Unauthorized | Log in to the corresponding system in the browser |
| 404 Not Found | Check skill URL — replace placeholders with real addresses |
| Network Error | Check that the system is accessible from the browser |

### Logs

Server logs: `~/.corpgateway/logs/`

```bash
# Windows
type %USERPROFILE%\.corpgateway\logs\cgw_mcp_2026-04-01.log

# Linux / macOS
tail -f ~/.corpgateway/logs/cgw_mcp_$(date +%Y-%m-%d).log
```

Extension logs: `chrome://extensions` → CorpGateway → **Inspect views: service worker**

---

## Component Diagram

```
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  OpenCode (AI Agent)                                         │
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
