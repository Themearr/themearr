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
UPDATER="/usr/local/bin/themearr-update"

info()  { echo "  [INFO]  $*"; }
ok()    { echo "  [OK]    $*"; }
error() { echo "  [ERROR] $*" >&2; exit 1; }

select_code_group() {
  # Root-run legacy deployments may predate the dedicated service account.
  # Fresh installs select the service group again after creating that account.
  CODE_GROUP=root
  if id -u themearr &>/dev/null; then CODE_GROUP=themearr; fi
}
select_code_group

validate_install_paths() {
  # A legacy service-owned parent can contain a data symlink. Never let root
  # ownership/mode repair follow it into another part of the host filesystem.
  [[ ! -L "$INSTALL_DIR" && ( ! -e "$INSTALL_DIR" || -d "$INSTALL_DIR" ) ]] \
    || error "Installation path must be a real directory: $INSTALL_DIR"
  [[ ! -L "$DATA_DIR" && ( ! -e "$DATA_DIR" || -d "$DATA_DIR" ) ]] \
    || error "Data path must be a real directory: $DATA_DIR"
}
validate_install_paths
if [[ -d "$INSTALL_DIR" ]]; then
  chown "root:$CODE_GROUP" "$INSTALL_DIR"
  chmod g+rx,go-w "$INSTALL_DIR"
  # Sealing the parent prevents a service user replacing data after this check.
  validate_install_paths
fi

# Keep the sudo-authorized helper outside the service-writable tree. Replacing the
# file also repairs helpers installed before arguments/download errors were handled.
write_updater() {
  local helper_tmp
  helper_tmp=$(mktemp /usr/local/bin/.themearr-update.XXXXXX)
  cat > "$helper_tmp" << 'UPDATER_EOF'
#!/usr/bin/env bash
set -euo pipefail
REPO="Themearr/themearr"
LOCAL="/opt/themearr/deploy.sh"
if [[ -f "$LOCAL" ]]; then
  exec bash "$LOCAL" "$@"
fi
REF="$(cat /opt/themearr/VERSION 2>/dev/null || echo main)"
[[ "$REF" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || REF="main"
TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT
# A partially downloaded shell script must never execute, even when curl fails.
curl -fsSL --connect-timeout 15 --max-time 120 \
  "https://raw.githubusercontent.com/${REPO}/${REF}/deploy.sh" -o "$TMP"
bash "$TMP" "$@"
UPDATER_EOF
  chmod 755 "$helper_tmp"
  chown root:root "$helper_tmp"
  mv -f "$helper_tmp" "$UPDATER"
  chown root:root "$UPDATER"
}

repair_permissions() {
  select_code_group
  validate_install_paths
  # Older installers made the entire tree service-owned, including deploy.sh.
  # Seal the parent first; only the existing data tree remains writable by the API.
  chown "root:$CODE_GROUP" "$INSTALL_DIR"
  chmod g+rx,go-w "$INSTALL_DIR"
  find "$INSTALL_DIR" -path "$DATA_DIR" -prune -o -exec chown -h "root:$CODE_GROUP" {} +
  # Previously service-owned 0600 overrides and 0700 directories still need
  # service read/traverse access after root takes ownership, without public access.
  find "$INSTALL_DIR" -path "$DATA_DIR" -prune -o -type d -exec chmod g+rx,go-w {} +
  find "$INSTALL_DIR" -path "$DATA_DIR" -prune -o -type f -exec chmod g+r,go-w {} +
  if id -u themearr &>/dev/null; then
    chown themearr:themearr "$DATA_DIR"
  fi
  chmod 700 "$DATA_DIR"
  write_updater
}

# A first upgraded startup can reach us through an old helper that drops arguments.
# The marker asks for ownership migration only: no network, runtime check or restart.
if [[ "${1:-}" == "--repair-permissions" || -e "$DATA_DIR/.repair-permissions" ]]; then
  repair_permissions
  rm -f "$DATA_DIR/.repair-permissions"
  ok "Native installation permissions repaired"
  exit 0
fi

# ── Resolve release asset ─────────────────────────────────────────────────────

TARGET="${1:-latest}"
if [[ "$TARGET" == "latest" ]]; then
  RELEASE_JSON=$(curl -fsSL --connect-timeout 15 --max-time 120 "https://api.github.com/repos/$GITHUB_REPO/releases/latest")
else
  RELEASE_JSON=$(curl -fsSL --connect-timeout 15 --max-time 120 "https://api.github.com/repos/$GITHUB_REPO/releases/tags/$TARGET")
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
  | grep "${ARCH_SUFFIX}.tar.gz\"" \
  | head -1 \
  | cut -d'"' -f4)

[[ -z "$ASSET_URL" ]] && error "No release asset found for $ARCH_SUFFIX in $TAG"

# Published SHA-256 checksum asset (themearr-<arch>.tar.gz.sha256), if present.
SHA_URL=$(echo "$RELEASE_JSON" \
  | grep '"browser_download_url"' \
  | grep "${ARCH_SUFFIX}.tar.gz.sha256" \
  | head -1 \
  | cut -d'"' -f4 || true)

info "Deploying Themearr $TAG ($ARCH_SUFFIX)"

# ── Verify the required .NET runtime ──────────────────────────────────────────
# The app targets net10.0. Installs provisioned earlier shipped with the .NET 9
# runtime, and dropping net10 binaries onto a 9.x runtime leaves the service unable
# to start. Check BEFORE touching any files and fail fast, so a mismatch leaves the
# running install working rather than half-updated. We deliberately do not
# auto-install it: piping a remote script into a root shell is exactly the
# supply-chain pattern this updater was hardened to avoid.

REQUIRED_DOTNET_MAJOR=10
if command -v dotnet &>/dev/null \
   && ! dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft.AspNetCore.App ${REQUIRED_DOTNET_MAJOR}\."; then
  error "This release needs the ASP.NET Core ${REQUIRED_DOTNET_MAJOR} runtime, which isn't installed.
          Nothing has been changed — your current install is still running.
          Install the runtime, then run the update again:
            curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
            bash /tmp/dotnet-install.sh --channel ${REQUIRED_DOTNET_MAJOR}.0 --runtime aspnetcore --install-dir /usr/share/dotnet"
fi

# The running API may still commit SQLite/WAL writes during this update.
# Leave that tree in place rather than restoring an increasingly stale snapshot.

# ── Download and extract ──────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
TMP="$STAGE/release.tar.gz"
info "Downloading release..."
curl -fsSL --connect-timeout 15 --max-time 600 "$ASSET_URL" -o "$TMP"

# Verify the tarball against the published SHA-256 before trusting it. This is the
# integrity check that makes piping the bootstrap script into a shell safe(r): even
# if the script is tampered with, a tampered/oversized tarball is rejected here.
if [[ -n "$SHA_URL" ]]; then
  EXPECTED=$(curl -fsSL --connect-timeout 15 --max-time 120 "$SHA_URL" | awk '{print $1}' | head -1)
  ACTUAL=$(sha256sum "$TMP" | awk '{print $1}')
  if [[ -z "$EXPECTED" || "$EXPECTED" != "$ACTUAL" ]]; then
    rm -f "$TMP"
    error "Checksum mismatch for $TAG ($ARCH_SUFFIX) — refusing to install. expected=$EXPECTED actual=$ACTUAL"
  fi
  ok "Checksum verified ($ACTUAL)"
else
  info "No published checksum for $TAG — skipping verification (older release)."
fi

mkdir "$STAGE/app"
tar -xzf "$TMP" -C "$STAGE/app" --strip-components=1 --no-same-owner --no-same-permissions
# Releases never own live data; reject any packaged data before merging files.
rm -rf "$STAGE/app/data"
# Keep native overrides and unknown keys when the release ships default settings.
for existing_config in "$INSTALL_DIR"/appsettings*.json; do
  if [[ -f "$existing_config" && ! -L "$existing_config" ]]; then
    rm -f -- "$STAGE/app/${existing_config##*/}"
  fi
done
# Existing application files may have legacy service ownership. Seal the parent
# before merging the completely extracted payload; extraction failure leaves it intact.
chown "root:$CODE_GROUP" "$INSTALL_DIR"
chmod g+rx,go-w "$INSTALL_DIR"
# Replace directory symlinks left by legacy service-writable installs before
# copying as root. Unlink payload files so mapped assemblies keep their old inode.
while IFS= read -r -d '' staged; do
  target="$INSTALL_DIR/${staged#"$STAGE/app/"}"
  [[ ! -L "$target" ]] || rm -f -- "$target"
  mkdir -p "$target"
  chown "root:$CODE_GROUP" "$target"
  chmod g+rx,go-w "$target"
done < <(find "$STAGE/app" -mindepth 1 -type d -print0)
while IFS= read -r -d '' staged; do
  target="$INSTALL_DIR/${staged#"$STAGE/app/"}"
  [[ ! -d "$target" || -L "$target" ]] || error "A directory blocks release file: $target"
  [[ ! -f "$target" && ! -L "$target" ]] || rm -f -- "$target"
  # Copy leaves individually so archive directory modes cannot relax existing
  # private configuration directories or the installation parent itself.
  cp -a -- "$staged" "$target"
done < <(find "$STAGE/app" \( -type f -o -type l \) -print0)
ok "Extracted to $INSTALL_DIR"

mkdir -p "$DATA_DIR"
repair_permissions

VERSION_TMP=$(mktemp "$INSTALL_DIR/.version.XXXXXX")
printf '%s\n' "$TAG" > "$VERSION_TMP"
chmod 644 "$VERSION_TMP"
mv -f "$VERSION_TMP" "$INSTALL_DIR/VERSION"

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
