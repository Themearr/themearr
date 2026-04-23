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
SERVICE_USER="themearr"
SERVICE_GROUP="themearr"
UPDATER="/usr/local/bin/themearr-update"
AUTH_ENV="$DATA_DIR/auth.env"
SUDOERS_FILE="/etc/sudoers.d/themearr"

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
  info "Installing system dependencies (ffmpeg, nodejs)..."
  apt-get install -y --no-install-recommends ffmpeg nodejs 2>&1 | grep -v "^$" || true
  # yt-dlp looks for "node" but Debian/Ubuntu installs it as "nodejs"
  if ! command -v node &>/dev/null && command -v nodejs &>/dev/null; then
    ln -sf "$(command -v nodejs)" /usr/local/bin/node
    info "Created node → nodejs symlink"
  fi
  ok "System dependencies installed"
fi

# ── yt-dlp (always install latest from GitHub — apt package is typically years out of date) ──
info "Installing latest yt-dlp from GitHub..."
curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp" \
  -o /usr/local/bin/yt-dlp
chmod a+rx /usr/local/bin/yt-dlp
ok "yt-dlp $(yt-dlp --version) installed"

# ── Download and extract ──────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR"
mkdir -p "$DATA_DIR"

TMP=$(mktemp /tmp/themearr-XXXXXX.tar.gz)
info "Downloading release..."
curl -fsSL "$ASSET_URL" -o "$TMP"
tar -xzf "$TMP" -C "$INSTALL_DIR" --strip-components=1 --no-same-owner --no-same-permissions
rm -f "$TMP"
ok "Extracted to $INSTALL_DIR"

echo "$TAG" > "$INSTALL_DIR/VERSION"

# ── Service user ──────────────────────────────────────────────────────────────
# Run as a dedicated non-root system user so a compromised API process cannot
# touch the rest of the filesystem.

if ! id -u "$SERVICE_USER" &>/dev/null; then
  useradd --system --no-create-home --home-dir "$DATA_DIR" \
          --shell /usr/sbin/nologin "$SERVICE_USER"
  ok "Created system user '$SERVICE_USER'"
fi

chown -R "$SERVICE_USER:$SERVICE_GROUP" "$INSTALL_DIR"
# Data dir holds the SQLite DB + auth token — lock down to the service user only.
chmod 700 "$DATA_DIR"

# ── Auth token ────────────────────────────────────────────────────────────────
# Generated once at install time, loaded from an EnvironmentFile by systemd.
# Preserve an existing token on re-run so clients don't need to be re-paired.

if [[ ! -s "$AUTH_ENV" ]]; then
  TOKEN=$(openssl rand -hex 32 2>/dev/null || head -c 32 /dev/urandom | xxd -p -c 64)
  umask 077
  printf 'THEMEARR_AUTH_TOKEN=%s\n' "$TOKEN" > "$AUTH_ENV"
  chown "$SERVICE_USER:$SERVICE_GROUP" "$AUTH_ENV"
  chmod 600 "$AUTH_ENV"
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
Environment="ASPNETCORE_URLS=http://127.0.0.1:8080"
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

cat > "$UPDATER" << 'UPDATER_EOF'
#!/usr/bin/env bash
curl -fsSL https://raw.githubusercontent.com/Themearr/themearr/main/deploy.sh | bash
UPDATER_EOF
chmod 755 "$UPDATER"
chown root:root "$UPDATER"

systemctl daemon-reload
systemctl enable --now "$SERVICE"
ok "Service started — Themearr $TAG is running on 127.0.0.1:8080"
