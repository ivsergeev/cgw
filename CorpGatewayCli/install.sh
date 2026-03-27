#!/usr/bin/env bash
# install.sh — build cgw and install to ~/bin (or /usr/local/bin with sudo)

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLI_DIR="$SCRIPT_DIR/CorpGatewayCli"
INSTALL_DIR="${1:-$HOME/.local/bin}"

echo "Building cgw..."
cd "$CLI_DIR"

# Detect platform
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)

case "$OS-$ARCH" in
  linux-x86_64)   RID="linux-x64" ;;
  linux-aarch64)  RID="linux-arm64" ;;
  darwin-x86_64)  RID="osx-x64" ;;
  darwin-arm64)   RID="osx-arm64" ;;
  *)
    echo "Unknown platform $OS-$ARCH, defaulting to linux-x64"
    RID="linux-x64" ;;
esac

dotnet publish -c Release -r "$RID" --self-contained -o ./dist/"$RID" \
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false

BINARY="$CLI_DIR/dist/$RID/cgw"
if [ ! -f "$BINARY" ]; then
  echo "Build failed: binary not found at $BINARY"
  exit 1
fi

mkdir -p "$INSTALL_DIR"
cp "$BINARY" "$INSTALL_DIR/cgw"
chmod +x "$INSTALL_DIR/cgw"

echo ""
echo "Installed to $INSTALL_DIR/cgw"

# Check PATH
if ! echo "$PATH" | grep -q "$INSTALL_DIR"; then
  echo ""
  echo "Add to your shell profile:"
  echo "  export PATH=\"\$PATH:$INSTALL_DIR\""
fi

echo ""
echo "Test: cgw health"
