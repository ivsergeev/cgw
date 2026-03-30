# cgw — CorpGateway CLI

Command-line tool for the AI agent to call corporate endpoints via CorpGateway.

## Install

**macOS / Linux:**
```bash
chmod +x install.sh
./install.sh
```

**Windows (PowerShell):**
```powershell
.\install.ps1
```

Or build manually:
```bash
cd CorpGatewayCli
dotnet publish -c Release -r osx-arm64 --self-contained -o ./dist
```

## Usage

```bash
# List available groups
cgw groups

# List all skills
cgw list

# List skills in a specific group
cgw list --group jira

# Get parameter schema for a skill
cgw schema jira_issue

# Invoke a skill
cgw invoke jira_issue key=PROJ-123
cgw invoke mm_search_posts team_id=abc123 terms="meeting notes"

# Check that CorpGateway is running
cgw health

# Generate AGENTS.md + opencode.json for OpenCode
cgw init-opencode
```

## Agent integration

Run `cgw init-opencode` in your project root. It generates:
- `AGENTS.md` — agent instructions (auto-read by OpenCode)
- `opencode.json` — tool definitions (`cgw_groups`, `cgw_list`, `cgw_schema`, `cgw_invoke`, `cgw_health`)

Agent workflow:
1. `cgw groups` — discover available groups (or `cgw list` for all skills)
2. `cgw list --group <id>` — focus on a specific group
3. `cgw schema <skill>` — get parameter details if needed
4. `cgw invoke <skill> key=value ...` — call a skill, output is JSON

## Configuration

Config is shared with the tray app at `%APPDATA%/CorpGateway/config.json`.

Override via environment variables:
```bash
export CGW_PORT=9876
export CGW_TOKEN=your-token-here
cgw list
```

Or point to a custom config:
```bash
cgw --config /path/to/config.json list
```

## Exit codes

| Code | Meaning                          |
|------|----------------------------------|
| 0    | Success                          |
| 1    | Usage error / API error response |
| 2    | Cannot connect to CorpGateway    |
