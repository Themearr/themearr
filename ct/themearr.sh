#!/usr/bin/env bash
source <(curl -fsSL https://raw.githubusercontent.com/community-scripts/ProxmoxVE/main/misc/build.func)
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
    $STD python3 -m venv /opt/themearr/venv
    $STD /opt/themearr/venv/bin/pip install --upgrade pip
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
