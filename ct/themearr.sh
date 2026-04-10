#!/usr/bin/env bash
# shellcheck source=/dev/null
source <(curl -fsSL https://raw.githubusercontent.com/community-scripts/ProxmoxVE/main/misc/build.func)

# ══════════════════════════════════════════════════════════════════════════════
# ██ DEVELOPMENT / FORK-TESTING OVERRIDES — DELETE BEFORE OPENING A PR ██
# ══════════════════════════════════════════════════════════════════════════════
#
#  These three exports redirect build.func to pull the install script from
#  the Themearr/themearr application repo instead of community-scripts/ProxmoxVE.
#  They are placed immediately after source so they are in scope before any
#  framework function (variables, build_container) can evaluate them.
#
#  HOW TO TEST:
#    1. Push both ct/themearr.sh and install/themearr-install.sh to your fork.
#    2. Verify the install script is live:
#         curl -I https://raw.githubusercontent.com/Themearr/themearr/main/install/themearr-install.sh
#    3. Wait 30–60 s for GitHub's raw CDN cache to refresh, then run:
#         bash -c "$(curl -fsSL https://raw.githubusercontent.com/Themearr/ProxmoxVE/main/ct/themearr.sh)"
#
#  BEFORE SUBMITTING THE PR — MANDATORY CHECKLIST:
#    [ ] Delete the three export lines below (GITHUB_USER, GITHUB_REPO, GITHUB_BRANCH)
#    [ ] Delete this entire comment block
#    [ ] Run: shellcheck ct/themearr.sh && shellcheck install/themearr-install.sh
#    [ ] Cherry-pick ONLY ct/themearr.sh + install/themearr-install.sh into a
#        clean branch based on upstream/main — NOT your fork's main branch.
#        The setup-fork.sh script modifies 600+ files; none of those go in the PR.
#
# ══════════════════════════════════════════════════════════════════════════════
export GITHUB_USER="Themearr"
export GITHUB_REPO="themearr"
export GITHUB_BRANCH="main"
# ══════════════════════════════════════════════════════════════════════════════

# Copyright (c) 2021-2026 community-scripts ORG
# Author: Devlin Cattermole
# License: MIT | https://github.com/community-scripts/ProxmoxVE/raw/main/LICENSE
# Source: https://github.com/Themearr/themearr

APP="Themearr"
var_tags="${var_tags:-media;plex;arr}"
var_cpu="${var_cpu:-2}"
var_ram="${var_ram:-1024}"
var_disk="${var_disk:-8}"
var_os="${var_os:-debian}"
var_version="${var_version:-13}"
var_unprivileged="${var_unprivileged:-1}"

header_info "$APP"
variables
color
catch_errors

function update_script() {
  header_info
  check_container_storage
  check_container_resources

  if [[ ! -d /opt/themearr ]]; then
    msg_error "No ${APP} Installation Found!"
    exit
  fi

  if check_for_gh_release "themearr" "Themearr/themearr"; then
    msg_info "Stopping Service"
    systemctl stop themearr
    msg_ok "Stopped Service"

    msg_info "Backing up Data"
    cp -r /opt/themearr/data /opt/themearr_data_backup
    msg_ok "Backed up Data"

    CLEAN_INSTALL=1 fetch_and_deploy_gh_release "themearr" "Themearr/themearr" "tarball" "latest" "/opt/themearr"

    msg_info "Rebuilding Python Environment"
    # shellcheck disable=SC2086
    $STD python3 -m venv /opt/themearr/venv
    # shellcheck disable=SC2086
    $STD /opt/themearr/venv/bin/pip install --upgrade pip
    # shellcheck disable=SC2086
    $STD /opt/themearr/venv/bin/pip install -r /opt/themearr/requirements.txt
    msg_ok "Rebuilt Python Environment"

    msg_info "Restoring Data"
    cp -r /opt/themearr_data_backup/. /opt/themearr/data
    rm -rf /opt/themearr_data_backup
    msg_ok "Restored Data"

    msg_info "Starting Service"
    systemctl start themearr
    msg_ok "Started Service"
    msg_ok "Updated Successfully!"
  fi
  exit
}

start
build_container
description

msg_ok "Completed Successfully!\n"
echo -e "${CREATING}${GN}${APP} setup has been successfully initialised!${CL}"
echo -e "${INFO}${YW} Access it using the following URL:${CL}"
echo -e "${TAB}${GATEWAY}${BGN}http://${IP}:8080${CL}"
