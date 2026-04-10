#!/usr/bin/env bash

# Exit immediately if a command exits with a non-zero status
set -e

echo ">>> Starting Themearr Deployment..."

# 1. Ensure git is installed
apt-get update -qq
apt-get install -y git -qq

# 2. Prepare a temporary source directory
SRC_DIR="/tmp/themearr_source"
if [ -d "$SRC_DIR" ]; then
    rm -rf "$SRC_DIR"
fi

# 3. Clone the repository to the temporary zone
echo ">>> Cloning repository..."
git clone https://github.com/Actuallbug2005/themearr.git "$SRC_DIR"
git -C "$SRC_DIR" rev-parse --short=12 HEAD > "$SRC_DIR/VERSION"

# 4. Safely wipe old application code (preserves database and .env)
echo ">>> Preparing target directory..."
rm -rf /opt/themearr/app
rm -f /opt/themearr/requirements.txt

# 5. Execute the native installation script
echo ">>> Executing native installer..."
cd "$SRC_DIR"
chmod +x install.sh
bash install.sh

# 6. Clean up temporary files
rm -rf "$SRC_DIR"

echo ">>> Deployment Complete!"
echo ">>> Open the web UI to finish setup for Radarr, API key, and local library paths."
echo ">>> Then use the UI update flow whenever a new GHCR package is published."
