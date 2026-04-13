#!/usr/bin/env bash
# Themearr fresh-install script
# Called by the ProxmoxVE install script after system deps are in place.
# Also suitable for any fresh Linux install where .NET runtime, ffmpeg,
# and yt-dlp are already available.
#
# Usage: bash install.sh [version]  (defaults to latest GitHub release)
set -euo pipefail

GITHUB_REPO="Themearr/themearr"
INSTALL_DIR="/opt/themearr"
DATA_DIR="$INSTALL_DIR/data"
SERVICE="themearr"
UPDATER="/usr/local/bin/themearr-update"

info()  { echo "  [INFO]  $*"; }
ok()    { echo "  [OK]    $*"; }
error() { echo "  [ERROR] $*" >&2; exit 1; }

# ── Resolve release asset ─────────────────────────────────────────────────────

TARGET="${1:-latest}"
if [[ "$TARGET" == "latest" ]]; then
  RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases/latest")
else
  RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases/tags/$TARGET")
fi

TAG=$(echo "$RELEASE_JSON" | grep '"tag_name"' | head -1 | cut -d'"' -f4)
[[ -z "$TAG" ]] && error "Could not determine release tag from GitHub API"

ARCH=$(uname -m)
case "$ARCH" in
  x86_64)  ARCH_SUFFIX="linux-x64" ;;
  aarch64) ARCH_SUFFIX="linux-arm64" ;;
  *)       error "Unsupported architecture: $ARCH" ;;
esac

ASSET_URL=$(echo "$RELEASE_JSON" \
  | grep '"browser_download_url"' \
  | grep "$ARCH_SUFFIX" \
  | head -1 \
  | cut -d'"' -f4)

[[ -z "$ASSET_URL" ]] && error "No release asset found for $ARCH_SUFFIX in $TAG. Check that the GitHub release includes a $ARCH_SUFFIX.tar.gz artifact."

info "Installing Themearr $TAG ($ARCH_SUFFIX)"

# ── System dependencies ───────────────────────────────────────────────────────

if command -v apt-get &>/dev/null; then
  info "Installing system dependencies (ffmpeg, yt-dlp, nodejs)..."
  apt-get install -y --no-install-recommends ffmpeg yt-dlp nodejs 2>&1 | grep -v "^$" || true
  ok "System dependencies installed"
fi

# ── Download and extract ──────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR"
mkdir -p "$DATA_DIR"

TMP=$(mktemp /tmp/themearr-XXXXXX.tar.gz)
info "Downloading release..."
curl -fsSL "$ASSET_URL" -o "$TMP"
tar -xzf "$TMP" -C "$INSTALL_DIR" --strip-components=1
rm -f "$TMP"
ok "Extracted to $INSTALL_DIR"

echo "$TAG" > "$INSTALL_DIR/VERSION"

# ── Systemd service ───────────────────────────────────────────────────────────

cat > /etc/systemd/system/themearr.service << EOF
[Unit]
Description=Themearr Service
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR
Environment="HOME=$DATA_DIR"
Environment="XDG_CACHE_HOME=$DATA_DIR/.cache"
Environment="DB_PATH=$DATA_DIR/themearr.db"
Environment="THEMEARR_VERSION_FILE=$INSTALL_DIR/VERSION"
Environment="ASPNETCORE_URLS=http://0.0.0.0:8080"
ExecStart=/usr/bin/dotnet $INSTALL_DIR/Themearr.API.dll
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# ── Updater helper ─────────────────────────────────────────────────────────────
# Fixed path so the in-app updater (UpdateService.cs) can always find it.

cat > "$UPDATER" << 'UPDATER_EOF'
#!/usr/bin/env bash
curl -fsSL https://raw.githubusercontent.com/Themearr/themearr/main/deploy.sh | bash
UPDATER_EOF
chmod +x "$UPDATER"

systemctl daemon-reload
systemctl enable --now "$SERVICE"
ok "Service started — Themearr $TAG is running on port 8080"
