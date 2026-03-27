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
# List available skills (call at session start, paste into agent context)
cgw list

# Get full parameter schema for a skill
cgw schema get_employee

# Invoke a skill
cgw invoke get_employee id=42
cgw invoke search_employees query="Smith" dept=Engineering

# Check that CorpGateway is running
cgw health

# Generate CLAUDE.md for Claude Code integration
cgw init-claude
```

## Claude Code integration

Run `cgw init-claude` in your project root. It generates a `CLAUDE.md` file that
Claude Code picks up automatically and knows how to call `cgw`.

Workflow inside Claude Code:
1. Agent calls `cgw list` to discover available skills
2. Uses `cgw schema <n>` if it needs parameter details
3. Calls `cgw invoke <n> [params...]` to fetch data
4. All output is JSON, piped directly to the agent

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
