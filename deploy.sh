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

# ── Backup data ───────────────────────────────────────────────────────────────
# Note: we do NOT stop the service first. On Linux, .NET assemblies are
# memory-mapped and can be replaced on disk while the process is running.
# The service is restarted at the end via systemd-run --no-block so the
# restart happens after this script exits (not while it's still a child of
# the running service process — which would kill us mid-deploy).

BACKUP=""
if [[ -d "$DATA_DIR" ]]; then
  BACKUP=$(mktemp -d /tmp/themearr_data_backup.XXXXXX)
  trap 'rm -rf "$BACKUP"' EXIT
  chmod 700 "$BACKUP"
  cp -r "$DATA_DIR/." "$BACKUP/"
  info "Data backed up"
fi

# ── Download and extract ──────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR"
TMP=$(mktemp /tmp/themearr-XXXXXX.tar.gz)
info "Downloading release..."
curl -fsSL "$ASSET_URL" -o "$TMP"
tar -xzf "$TMP" -C "$INSTALL_DIR" --strip-components=1 --no-same-owner --no-same-permissions
rm -f "$TMP"
ok "Extracted to $INSTALL_DIR"

# ── Restore data ──────────────────────────────────────────────────────────────

mkdir -p "$DATA_DIR"
if [[ -n "$BACKUP" ]]; then
  cp -r "$BACKUP/." "$DATA_DIR/"
  chmod 700 "$DATA_DIR"
  [[ -f "$DATA_DIR/auth.env" ]] && chmod 600 "$DATA_DIR/auth.env"
  rm -rf "$BACKUP"
  ok "Data restored"
fi

# Re-assert ownership after extraction (tar wrote files as root; the service
# runs as 'themearr' and must be able to read its own install directory).
if id -u themearr &>/dev/null; then
  chown -R themearr:themearr "$INSTALL_DIR"
fi

echo "$TAG" > "$INSTALL_DIR/VERSION"

# ── Schedule restart ──────────────────────────────────────────────────────────
# Use systemd-run --no-block to restart the service in a new transient unit,
# completely detached from this script's process group. This means the restart
# fires after this script exits cleanly — even if this script is a child of
# the service being restarted.

ok "Themearr $TAG deployed — scheduling service restart"
systemctl daemon-reload
if command -v systemd-run &>/dev/null; then
  # Delay 5 s so the running API process has time to write its "finished" state
  # to the database and serve one final status poll before the restart kills it.
  systemd-run --no-block --unit="themearr-restart-$$" \
    --description="Restart Themearr after update" \
    /bin/sh -c "sleep 5 && systemctl restart $SERVICE"
else
  # Fallback for environments without systemd-run (shouldn't happen on Debian)
  (sleep 5 && systemctl restart "$SERVICE") </dev/null &>/dev/null &
fi
