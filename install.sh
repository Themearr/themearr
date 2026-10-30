#!/usr/bin/env bash
# Themearr fresh-install script
# Called by the ProxmoxVE install script after system deps are in place.
# Also suitable for any fresh Linux install where the .NET runtime is available.
# (Unprocessed downloads need no encoder; optional processing requires ffmpeg.)
#
# Usage: bash install.sh [version]  (defaults to latest GitHub release)
#
# Environment variables:
#   THEMEARR_BIND  — address the API listens on (default: 127.0.0.1).
#                    Set to 0.0.0.0 to expose on the LAN (bearer auth still
#                    required; no TLS — put a reverse proxy in front for WAN).
set -euo pipefail

GITHUB_REPO="Themearr/themearr"
INSTALL_DIR="/opt/themearr"
DATA_DIR="$INSTALL_DIR/data"
SERVICE="themearr"
SERVICE_USER="themearr"
SERVICE_GROUP="themearr"
UPDATER="/usr/local/bin/themearr-update"
AUTH_ENV="$DATA_DIR/auth.env"
SUDOERS_FILE="/etc/sudoers.d/themearr"
BIND_ADDR="${THEMEARR_BIND:-127.0.0.1}"
LISTEN_PORT=8080

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

[[ -z "$ASSET_URL" ]] && error "No release asset found for $ARCH_SUFFIX in $TAG. Check that the GitHub release includes a $ARCH_SUFFIX.tar.gz artifact."

# Published SHA-256 checksum asset (themearr-<arch>.tar.gz.sha256), if present.
SHA_URL=$(echo "$RELEASE_JSON" \
  | grep '"browser_download_url"' \
  | grep "${ARCH_SUFFIX}.tar.gz.sha256" \
  | head -1 \
  | cut -d'"' -f4 || true)

info "Installing Themearr $TAG ($ARCH_SUFFIX)"

# ── System dependencies ───────────────────────────────────────────────────────
# Keep existing installs dependency-free unless processing is explicitly enabled.
if command -v ffmpeg >/dev/null 2>&1 && command -v ffprobe >/dev/null 2>&1; then
  info "Optional audio processing tools are available."
else
  info "Optional audio processing needs ffmpeg and ffprobe. On Debian/Ubuntu: apt-get install ffmpeg"
  info "Leave audio processing disabled until both tools are installed."
fi

# ── Download and extract ──────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR"
mkdir -p "$DATA_DIR"

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
TMP="$STAGE/release.tar.gz"
info "Downloading release..."
curl -fsSL --connect-timeout 15 --max-time 600 "$ASSET_URL" -o "$TMP"

# Verify the tarball against the published SHA-256 before extracting.
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

VERSION_TMP=$(mktemp "$INSTALL_DIR/.version.XXXXXX")
printf '%s\n' "$TAG" > "$VERSION_TMP"
chmod 644 "$VERSION_TMP"
mv -f "$VERSION_TMP" "$INSTALL_DIR/VERSION"

# ── Service user ──────────────────────────────────────────────────────────────
# Run as a dedicated non-root system user so a compromised API process cannot
# touch the rest of the filesystem.

if ! id -u "$SERVICE_USER" &>/dev/null; then
  useradd --system --no-create-home --home-dir "$DATA_DIR" \
          --shell /usr/sbin/nologin "$SERVICE_USER"
  ok "Created system user '$SERVICE_USER'"
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

repair_permissions

# ── Auth token ────────────────────────────────────────────────────────────────
# Generated once at install time, loaded from an EnvironmentFile by systemd.
# Preserve an existing token on re-run so clients don't need to be re-paired.

if [[ ! -s "$AUTH_ENV" ]]; then
  TOKEN=$(openssl rand -hex 32 2>/dev/null || head -c 32 /dev/urandom | xxd -p -c 64)
  umask 077
  AUTH_TMP=$(mktemp "$STAGE/auth.XXXXXX")
  printf 'THEMEARR_AUTH_TOKEN=%s\n' "$TOKEN" > "$AUTH_TMP"
  chown "$SERVICE_USER:$SERVICE_GROUP" "$AUTH_TMP"
  chmod 600 "$AUTH_TMP"
  mv -f "$AUTH_TMP" "$AUTH_ENV"
  echo
  echo "  ================================================================"
  echo "  Access token (save this — it won't be shown again):"
  echo "    $TOKEN"
  echo "  Stored at: $AUTH_ENV"
  echo "  ================================================================"
  echo
else
  ok "Access token already exists at $AUTH_ENV — preserving"
fi

# ── Sudoers drop-in ───────────────────────────────────────────────────────────
# The in-app updater (POST /api/update) needs to run the update helper as root.
# Scope the sudo permission to exactly that one binary — nothing else.

cat > "$SUDOERS_FILE" << EOF
$SERVICE_USER ALL=(root) NOPASSWD: $UPDATER
EOF
chmod 440 "$SUDOERS_FILE"
# Validate syntax — visudo -c returns non-zero on bad file, which aborts the install.
visudo -cf "$SUDOERS_FILE" >/dev/null

# ── Systemd service ───────────────────────────────────────────────────────────
# Binds to loopback only by default. Put a reverse proxy (nginx/caddy) in front
# for remote access — do NOT change this to 0.0.0.0 without adding TLS + auth.

cat > /etc/systemd/system/themearr.service << EOF
[Unit]
Description=Themearr Service
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_GROUP
WorkingDirectory=$INSTALL_DIR
EnvironmentFile=$AUTH_ENV
Environment="HOME=$DATA_DIR"
Environment="XDG_CACHE_HOME=$DATA_DIR/.cache"
Environment="DB_PATH=$DATA_DIR/themearr.db"
Environment="THEMEARR_VERSION_FILE=$INSTALL_DIR/VERSION"
Environment="ASPNETCORE_URLS=http://$BIND_ADDR:$LISTEN_PORT"
ExecStart=/usr/bin/dotnet $INSTALL_DIR/Themearr.API.dll
Restart=on-failure
RestartSec=5
# Light hardening — NoNewPrivileges is intentionally off so the updater's
# sudo call still works. If you ever drop the in-app updater, switch it on.
PrivateTmp=yes
ProtectControlGroups=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes

[Install]
WantedBy=multi-user.target
EOF

# ── Updater helper ─────────────────────────────────────────────────────────────
# Fixed path so the in-app updater (UpdateService.cs) can always find it.


systemctl daemon-reload
systemctl enable --now "$SERVICE"
ok "Service started — Themearr $TAG is running on $BIND_ADDR:$LISTEN_PORT"
echo
echo "  Next step — enable downloads:"
echo "    Theme downloads use the youtube-mp36 RapidAPI. After signing in,"
echo "    open Settings → RapidAPI and add your RapidAPI key + username."
echo "    Browsing works without it; downloads stay disabled until both are set."
if [[ "$BIND_ADDR" == "0.0.0.0" ]]; then
  echo "  [WARN]  Bound to 0.0.0.0 — the API is reachable from the LAN without TLS."
  echo "          The bearer token is still required, but consider putting a reverse"
  echo "          proxy (caddy/nginx) in front before exposing this to the internet."
fi
