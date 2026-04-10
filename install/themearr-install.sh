#!/usr/bin/env bash

# Copyright (c) 2021-2026 community-scripts ORG
# Author: Devlin Cattermole
# License: MIT | https://github.com/community-scripts/ProxmoxVE/raw/main/LICENSE
# Source: https://github.com/Themearr/themearr

# shellcheck source=/dev/null
source /dev/stdin <<<"$FUNCTIONS_FILE_PATH"
color
verb_ip6
catch_errors
setting_up_container
network_check
update_os

msg_info "Installing Dependencies"
# shellcheck disable=SC2086
$STD apt-get install -y \
  python3-venv \
  python3-pip \
  curl \
  unzip
msg_ok "Installed Dependencies"

setup_ffmpeg

msg_info "Installing Deno"
# $STD cannot wrap a pipe directly (stdout redirect breaks pipe); wrap in bash -c to silence both sides
# shellcheck disable=SC2086
$STD bash -c "curl -fsSL https://deno.land/install.sh | DENO_INSTALL=/usr/local sh"
msg_ok "Installed Deno"

fetch_and_deploy_gh_release "yt-dlp" "yt-dlp/yt-dlp" "singlefile" "latest" "/usr/local/bin" "yt-dlp"
chmod +x /usr/local/bin/yt-dlp

fetch_and_deploy_gh_release "themearr" "Themearr/themearr" "tarball" "latest" "/opt/themearr"

msg_info "Setting up Application"
mkdir -p /opt/themearr/data
# shellcheck disable=SC2086
$STD python3 -m venv /opt/themearr/venv
# shellcheck disable=SC2086
$STD /opt/themearr/venv/bin/pip install --upgrade pip
# shellcheck disable=SC2086
$STD /opt/themearr/venv/bin/pip install -r /opt/themearr/requirements.txt
msg_ok "Set up Application"

msg_info "Creating Service"
cat <<EOF >/etc/systemd/system/themearr.service
[Unit]
Description=Themearr Service
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/themearr
Environment="HOME=/opt/themearr/data"
Environment="XDG_CACHE_HOME=/opt/themearr/data/.cache"
ExecStart=/opt/themearr/venv/bin/python -m uvicorn app.main:app --host 0.0.0.0 --port 8080 --no-access-log
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
systemctl enable -q --now themearr
msg_ok "Created Service"

motd_ssh
customize
cleanup_lxc
