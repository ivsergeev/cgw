#!/usr/bin/env bash
set -euo pipefail

# CorpGateway Installer Build Script (Linux / macOS)
# Prerequisites: .NET 8 SDK
# Linux extras: dpkg-deb (for .deb), rpmbuild (for .rpm, optional)
# macOS extras: hdiutil (built-in, for .dmg)

APP_NAME="CorpGateway"
APP_VERSION="1.0.0"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="$SCRIPT_DIR/publish"
OUTPUT_DIR="$SCRIPT_DIR/output"

# Detect platform
ARCH="$(uname -m)"
OS="$(uname -s)"

case "$OS-$ARCH" in
    Linux-x86_64)   RID="linux-x64" ;;
    Linux-aarch64)  RID="linux-arm64" ;;
    Darwin-x86_64)  RID="osx-x64" ;;
    Darwin-arm64)   RID="osx-arm64" ;;
    *)              echo "Unsupported platform: $OS-$ARCH"; exit 1 ;;
esac

echo "=== CorpGateway Installer Build ($RID) ==="

# Clean
rm -rf "$PUBLISH_DIR" "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Publish Gateway
echo ""
echo "[1/3] Publishing Gateway..."
dotnet publish "$ROOT_DIR/CorpGateway/CorpGateway.csproj" \
    -c Release -r "$RID" --self-contained \
    -o "$PUBLISH_DIR/gateway"

# Publish CLI
echo ""
echo "[2/3] Publishing CLI..."
dotnet publish "$ROOT_DIR/CorpGatewayCli/CorpGatewayCli.csproj" \
    -c Release -r "$RID" --self-contained \
    -o "$PUBLISH_DIR/cli"

# Rename CLI binary
if [ -f "$PUBLISH_DIR/cli/CorpGatewayCli" ]; then
    mv "$PUBLISH_DIR/cli/CorpGatewayCli" "$PUBLISH_DIR/cli/cgw"
elif [ -f "$PUBLISH_DIR/cli/CorpGatewayCli.exe" ]; then
    mv "$PUBLISH_DIR/cli/CorpGatewayCli.exe" "$PUBLISH_DIR/cli/cgw"
fi

echo ""
echo "[3/3] Building packages..."

# ── tar.gz (universal) ────────────────────────────────────────────────
build_tarball() {
    local tarname="corpgateway-${APP_VERSION}-${RID}.tar.gz"
    local staging="$PUBLISH_DIR/tarball/corpgateway"
    mkdir -p "$staging/bin"
    cp -r "$PUBLISH_DIR/gateway/." "$staging/"
    cp "$PUBLISH_DIR/cli/cgw" "$staging/bin/cgw" 2>/dev/null || true
    chmod +x "$staging/CorpGateway" "$staging/bin/cgw" 2>/dev/null || true
    tar -czf "$OUTPUT_DIR/$tarname" -C "$PUBLISH_DIR/tarball" corpgateway
    echo "  tar.gz: $OUTPUT_DIR/$tarname"
}

# ── .deb (Debian/Ubuntu) ─────────────────────────────────────────────
build_deb() {
    if ! command -v dpkg-deb &>/dev/null; then
        echo "  .deb: skipped (dpkg-deb not found)"
        return
    fi

    local debname="corpgateway_${APP_VERSION}_amd64.deb"
    [ "$ARCH" = "aarch64" ] && debname="corpgateway_${APP_VERSION}_arm64.deb"

    local debarch="amd64"
    [ "$ARCH" = "aarch64" ] && debarch="arm64"

    local staging="$PUBLISH_DIR/deb"
    mkdir -p "$staging/DEBIAN"
    mkdir -p "$staging/opt/corpgateway"
    mkdir -p "$staging/usr/local/bin"
    mkdir -p "$staging/usr/share/applications"

    # Control file
    cat > "$staging/DEBIAN/control" <<CTRL
Package: corpgateway
Version: ${APP_VERSION}
Section: utils
Priority: optional
Architecture: ${debarch}
Maintainer: CorpGateway
Description: Corporate API gateway for AI agents
 Exposes corporate JSON endpoints to AI agents via a secure local HTTP API.
 Proxies requests through the browser session for seamless authentication.
CTRL

    # Post-install: symlink CLI
    cat > "$staging/DEBIAN/postinst" <<'POST'
#!/bin/sh
ln -sf /opt/corpgateway/cli/cgw /usr/local/bin/cgw
chmod +x /opt/corpgateway/CorpGateway /opt/corpgateway/cli/cgw 2>/dev/null || true
POST
    chmod 755 "$staging/DEBIAN/postinst"

    # Post-remove: clean symlink
    cat > "$staging/DEBIAN/postrm" <<'PORM'
#!/bin/sh
rm -f /usr/local/bin/cgw
PORM
    chmod 755 "$staging/DEBIAN/postrm"

    # Desktop entry
    cat > "$staging/usr/share/applications/corpgateway.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=CorpGateway
Exec=/opt/corpgateway/CorpGateway
Icon=/opt/corpgateway/Assets/app.ico
Categories=Utility;Development;
Comment=Corporate API gateway for AI agents
DESK

    # Copy files
    cp -r "$PUBLISH_DIR/gateway/." "$staging/opt/corpgateway/"
    mkdir -p "$staging/opt/corpgateway/cli"
    cp "$PUBLISH_DIR/cli/cgw" "$staging/opt/corpgateway/cli/cgw" 2>/dev/null || true

    dpkg-deb --build "$staging" "$OUTPUT_DIR/$debname"
    echo "  .deb:    $OUTPUT_DIR/$debname"
}

# ── .dmg (macOS) ─────────────────────────────────────────────────────
build_dmg() {
    if ! command -v hdiutil &>/dev/null; then
        echo "  .dmg: skipped (not macOS)"
        return
    fi

    local dmgname="CorpGateway-${APP_VERSION}-${RID}.dmg"
    local staging="$PUBLISH_DIR/dmg"
    local appdir="$staging/CorpGateway.app"

    # Create .app bundle structure
    mkdir -p "$appdir/Contents/MacOS"
    mkdir -p "$appdir/Contents/Resources"

    # Copy gateway into .app
    cp -r "$PUBLISH_DIR/gateway/." "$appdir/Contents/MacOS/"
    chmod +x "$appdir/Contents/MacOS/CorpGateway"

    # Copy CLI alongside .app
    mkdir -p "$staging/cli"
    cp "$PUBLISH_DIR/cli/cgw" "$staging/cli/cgw" 2>/dev/null || true
    chmod +x "$staging/cli/cgw" 2>/dev/null || true

    # Info.plist
    cat > "$appdir/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>CorpGateway</string>
    <key>CFBundleDisplayName</key>
    <string>CorpGateway</string>
    <key>CFBundleIdentifier</key>
    <string>com.corpgateway</string>
    <key>CFBundleVersion</key>
    <string>${APP_VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${APP_VERSION}</string>
    <key>CFBundleExecutable</key>
    <string>CorpGateway</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSUIElement</key>
    <true/>
</dict>
</plist>
PLIST

    # README for CLI installation
    cat > "$staging/Install CLI.txt" <<README
To install the cgw CLI tool, run:

  sudo cp "$(pwd)/cli/cgw" /usr/local/bin/cgw

Then verify:

  cgw --help
README

    # Create DMG
    hdiutil create -volname "CorpGateway" \
        -srcfolder "$staging" \
        -ov -format UDZO \
        "$OUTPUT_DIR/$dmgname"
    echo "  .dmg:    $OUTPUT_DIR/$dmgname"
}

# Build packages based on OS
build_tarball

if [ "$OS" = "Linux" ]; then
    build_deb
fi

if [ "$OS" = "Darwin" ]; then
    build_dmg
fi

echo ""
echo "=== Done! ==="
ls -lh "$OUTPUT_DIR/"
