#!/usr/bin/env bash
set -euo pipefail

# cgw_mcp daemon installer (Linux / macOS)
#
# Installs cgw_mcp as a user-level service:
# - Linux:  systemd user service (runs --foreground under systemd)
# - macOS:  launchd LaunchAgent (runs --foreground under launchd)
#
# Usage:
#   ./install.sh           # install and start
#   ./install.sh uninstall # stop and remove

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVICE_NAME="cgw-mcp"
NODE_BIN="$(command -v node 2>/dev/null || true)"
ENTRY="$SCRIPT_DIR/index.js"
OS="$(uname -s)"

# ── Checks ──────────────────────────────────────────────────

if [ -z "$NODE_BIN" ]; then
    echo "Error: node not found in PATH"
    echo "Install Node.js 18+ first: https://nodejs.org"
    exit 1
fi

if [ ! -f "$ENTRY" ]; then
    echo "Error: $ENTRY not found"
    exit 1
fi

# Install npm deps if needed
if [ ! -d "$SCRIPT_DIR/node_modules" ]; then
    echo "Installing dependencies..."
    cd "$SCRIPT_DIR" && npm install --production
fi

# ── Linux (systemd) ────────────────────────────────────────

install_linux() {
    local unit_dir="$HOME/.config/systemd/user"
    local unit_file="$unit_dir/$SERVICE_NAME.service"
    mkdir -p "$unit_dir"

    cat > "$unit_file" <<EOF
[Unit]
Description=CorpGateway MCP Server
After=network.target

[Service]
Type=simple
ExecStart=$NODE_BIN $ENTRY --foreground
ExecStop=$NODE_BIN $ENTRY stop
Restart=on-failure
RestartSec=5
WorkingDirectory=$SCRIPT_DIR
Environment=NODE_ENV=production

[Install]
WantedBy=default.target
EOF

    systemctl --user daemon-reload
    systemctl --user enable "$SERVICE_NAME"
    systemctl --user start "$SERVICE_NAME"

    echo "✓ Installed systemd user service: $unit_file"
    echo "✓ Service started"
    echo ""
    echo "Commands:"
    echo "  systemctl --user status $SERVICE_NAME"
    echo "  systemctl --user restart $SERVICE_NAME"
    echo "  systemctl --user stop $SERVICE_NAME"
    echo "  journalctl --user -u $SERVICE_NAME -f"
    echo ""
    echo "Or use built-in daemon mode directly:"
    echo "  node $ENTRY status"
    echo "  node $ENTRY start     # background daemon"
    echo "  node $ENTRY stop"
    echo "  node $ENTRY restart"
}

uninstall_linux() {
    systemctl --user stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl --user disable "$SERVICE_NAME" 2>/dev/null || true
    rm -f "$HOME/.config/systemd/user/$SERVICE_NAME.service"
    systemctl --user daemon-reload
    echo "✓ Service removed"
}

# ── macOS (launchd) ─────────────────────────────────────────

install_macos() {
    local plist_dir="$HOME/Library/LaunchAgents"
    local plist_file="$plist_dir/com.$SERVICE_NAME.plist"
    mkdir -p "$plist_dir"

    cat > "$plist_file" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.$SERVICE_NAME</string>
    <key>ProgramArguments</key>
    <array>
        <string>$NODE_BIN</string>
        <string>$ENTRY</string>
        <string>--foreground</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$SCRIPT_DIR</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$HOME/.corpgateway/logs/cgw_mcp_launchd.log</string>
    <key>StandardErrorPath</key>
    <string>$HOME/.corpgateway/logs/cgw_mcp_launchd.log</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>NODE_ENV</key>
        <string>production</string>
    </dict>
</dict>
</plist>
EOF

    # Stop if already running
    launchctl unload "$plist_file" 2>/dev/null || true
    launchctl load "$plist_file"

    echo "✓ Installed LaunchAgent: $plist_file"
    echo "✓ Service started"
    echo ""
    echo "Commands:"
    echo "  launchctl list | grep $SERVICE_NAME"
    echo "  launchctl unload $plist_file   # stop"
    echo "  launchctl load $plist_file     # start"
    echo "  tail -f ~/.corpgateway/logs/cgw_mcp_launchd.log"
    echo ""
    echo "Or use built-in daemon mode directly:"
    echo "  node $ENTRY status"
    echo "  node $ENTRY start     # background daemon"
    echo "  node $ENTRY stop"
    echo "  node $ENTRY restart"
}

uninstall_macos() {
    local plist_file="$HOME/Library/LaunchAgents/com.$SERVICE_NAME.plist"
    launchctl unload "$plist_file" 2>/dev/null || true
    rm -f "$plist_file"
    echo "✓ Service removed"
}

# ── Main ────────────────────────────────────────────────────

ACTION="${1:-install}"

case "$OS" in
    Linux)
        if [ "$ACTION" = "uninstall" ]; then
            uninstall_linux
        else
            install_linux
        fi
        ;;
    Darwin)
        if [ "$ACTION" = "uninstall" ]; then
            uninstall_macos
        else
            install_macos
        fi
        ;;
    *)
        echo "Unsupported OS: $OS"
        echo "Use install.ps1 for Windows"
        exit 1
        ;;
esac

# Show config
CONFIG="$HOME/.corpgateway/cgw_mcp.json"
if [ -f "$CONFIG" ]; then
    echo ""
    echo "Config: $CONFIG"
    echo "Logs:   $HOME/.corpgateway/logs/"
    echo ""
    TOKEN=$(node -e "console.log(JSON.parse(require('fs').readFileSync('$CONFIG','utf8')).token)" 2>/dev/null || true)
    EXT_TOKEN=$(node -e "console.log(JSON.parse(require('fs').readFileSync('$CONFIG','utf8')).extensionToken)" 2>/dev/null || true)
    PORT=$(node -e "console.log(JSON.parse(require('fs').readFileSync('$CONFIG','utf8')).port)" 2>/dev/null || true)
    echo "Port:            $PORT"
    echo "Agent token:     $TOKEN"
    echo "Extension token: $EXT_TOKEN"
fi
