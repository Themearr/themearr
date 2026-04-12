#!/usr/bin/env bash
# Themearr update / redeploy script
# Used by: in-app updater, /usr/local/bin/themearr-update, ProxmoxVE update
# For a fresh install use install.sh instead (no service stop / data backup needed).
#
# Usage: bash deploy.sh [version]  (defaults to latest GitHub release)
set -euo pipefail

GITHUB_REPO="Themearr/themearr"
INSTALL_DIR="/opt/themearr"
DATA_DIR="$INSTALL_DIR/data"
SERVICE="themearr"

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

[[ -z "$ASSET_URL" ]] && error "No release asset found for $ARCH_SUFFIX in $TAG"

info "Deploying Themearr $TAG ($ARCH_SUFFIX)"

# ── Stop service ──────────────────────────────────────────────────────────────

if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  info "Stopping service..."
  systemctl stop "$SERVICE"
fi

# ── Backup data ───────────────────────────────────────────────────────────────

BACKUP=""
if [[ -d "$DATA_DIR" ]]; then
  BACKUP="/tmp/themearr_data_backup_$$"
  cp -r "$DATA_DIR" "$BACKUP"
  info "Data backed up"
fi

# ── Download and extract ──────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR"
TMP=$(mktemp /tmp/themearr-XXXXXX.tar.gz)
info "Downloading release..."
curl -fsSL "$ASSET_URL" -o "$TMP"
tar -xzf "$TMP" -C "$INSTALL_DIR" --strip-components=1
rm -f "$TMP"
ok "Extracted to $INSTALL_DIR"

# ── Restore data ──────────────────────────────────────────────────────────────

mkdir -p "$DATA_DIR"
if [[ -n "$BACKUP" ]]; then
  cp -r "$BACKUP/." "$DATA_DIR/"
  rm -rf "$BACKUP"
  ok "Data restored"
fi

echo "$TAG" > "$INSTALL_DIR/VERSION"

# ── Reload and start ──────────────────────────────────────────────────────────

systemctl daemon-reload
systemctl restart "$SERVICE"
ok "Themearr $TAG deployed successfully"
