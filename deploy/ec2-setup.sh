#!/usr/bin/env bash
# =============================================================================
# PromoEngine - one-time EC2 host preparation
#
# Run this ONCE on a fresh EC2 instance, before the first GitHub Actions deploy.
# It installs Docker, installs the Compose plugin, creates the deployment
# directory and pre-creates the PostgreSQL volume and the Docker network so
# that the very first `compose up` has nothing left to discover.
#
#   curl -fsSL -o ec2-setup.sh <raw url>   # or scp it across
#   chmod +x ec2-setup.sh && ./ec2-setup.sh
#
# Supports Amazon Linux 2023 and Ubuntu 22.04/24.04.
# =============================================================================
set -Eeuo pipefail

APP_DIR="/opt/promoengine"
POSTGRES_VOLUME="promoengine-postgres-data"
DOCKER_NETWORK="promoengine"

log() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die() { printf '\033[1;31m[fail] %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] && die "Run as the normal login user (ec2-user / ubuntu), not root. The script calls sudo itself."

# shellcheck disable=SC1091
. /etc/os-release
log "Detected ${PRETTY_NAME}"

# -----------------------------------------------------------------------------
# 1. Docker Engine + Compose plugin
# -----------------------------------------------------------------------------
if command -v docker >/dev/null 2>&1; then
  log "Docker already installed: $(docker --version)"
else
  case "${ID}" in
    amzn)
      log "Installing Docker from the Amazon Linux repositories"
      sudo dnf update -y
      sudo dnf install -y docker
      ;;
    ubuntu|debian)
      log "Installing Docker from the official Docker repository"
      sudo apt-get update -y
      sudo apt-get install -y ca-certificates curl gnupg

      sudo install -m 0755 -d /etc/apt/keyrings
      curl -fsSL "https://download.docker.com/linux/${ID}/gpg" \
        | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
      sudo chmod a+r /etc/apt/keyrings/docker.gpg

      echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/${ID} $(. /etc/os-release && echo "${VERSION_CODENAME}") stable" \
        | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

      sudo apt-get update -y
      sudo apt-get install -y docker-ce docker-ce-cli containerd.io \
                              docker-buildx-plugin docker-compose-plugin
      ;;
    *)
      die "Unsupported distribution '${ID}'. Install Docker manually, then re-run."
      ;;
  esac
fi

log "Enabling Docker at boot"
sudo systemctl enable --now docker

# Compose v2 ships as a plugin. Amazon Linux packages Docker without it, so it
# is fetched directly into the CLI plugin directory when missing.
if docker compose version >/dev/null 2>&1; then
  log "Docker Compose present: $(docker compose version --short)"
else
  log "Installing the Docker Compose plugin"
  COMPOSE_VERSION="v2.32.4"
  ARCH="$(uname -m)"
  sudo mkdir -p /usr/local/lib/docker/cli-plugins
  sudo curl -fsSL \
    "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-${ARCH}" \
    -o /usr/local/lib/docker/cli-plugins/docker-compose
  sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose
  docker compose version
fi

# -----------------------------------------------------------------------------
# 2. Let the deploy user drive Docker without sudo
#
# The GitHub Actions SSH session is non-interactive and cannot answer a sudo
# password prompt, so the login user must be in the docker group.
# -----------------------------------------------------------------------------
if id -nG "${USER}" | tr ' ' '\n' | grep -qx docker; then
  log "${USER} is already in the docker group"
else
  log "Adding ${USER} to the docker group"
  sudo usermod -aG docker "${USER}"
  NEEDS_RELOGIN=1
fi

# -----------------------------------------------------------------------------
# 3. Deployment directory
#
# docker-compose.yml, deploy.sh and .env all live here. The pipeline copies the
# first two on every run and writes the third; nothing else belongs in it.
# -----------------------------------------------------------------------------
log "Creating ${APP_DIR}"
sudo mkdir -p "${APP_DIR}"
sudo chown "${USER}:${USER}" "${APP_DIR}"
chmod 750 "${APP_DIR}"

# -----------------------------------------------------------------------------
# 4. Persistent storage and network
#
# Compose would create both on first use. Creating them here means they exist
# with the expected names from the start, and it is an explicit statement that
# the volume is host state that outlives any individual deployment.
# -----------------------------------------------------------------------------
if docker volume inspect "${POSTGRES_VOLUME}" >/dev/null 2>&1; then
  log "PostgreSQL volume ${POSTGRES_VOLUME} already exists - leaving it alone"
else
  log "Creating PostgreSQL volume ${POSTGRES_VOLUME}"
  docker volume create "${POSTGRES_VOLUME}"
fi

if docker network inspect "${DOCKER_NETWORK}" >/dev/null 2>&1; then
  log "Network ${DOCKER_NETWORK} already exists"
else
  log "Creating network ${DOCKER_NETWORK}"
  docker network create --driver bridge "${DOCKER_NETWORK}"
fi

# -----------------------------------------------------------------------------
# 5. Housekeeping
#
# Without a cap, json-file logs grow until the root volume fills and every
# container stops at once. The Compose file sets per-service limits; this sets
# the daemon default so anything started outside Compose is covered too.
# -----------------------------------------------------------------------------
if [[ ! -f /etc/docker/daemon.json ]]; then
  log "Applying default log rotation for the Docker daemon"
  sudo mkdir -p /etc/docker
  sudo tee /etc/docker/daemon.json > /dev/null <<'JSON'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "live-restore": true
}
JSON
  sudo systemctl restart docker
fi

log "Host preparation complete"
cat <<SUMMARY

  Deployment directory : ${APP_DIR}
  PostgreSQL volume    : ${POSTGRES_VOLUME}
  Docker network       : ${DOCKER_NETWORK}

  Next:
    1. Security group inbound: 22 from your IP, 80 from 0.0.0.0/0.
       Do NOT open 5432 - PostgreSQL is reachable only inside the Docker network.
    2. Add the repository secrets, then push to main. The pipeline copies
       docker-compose.yml and deploy.sh here and starts the stack.
    3. Watch the first deploy with:
         cd ${APP_DIR} && docker compose logs -f backend

SUMMARY

if [[ -n "${NEEDS_RELOGIN:-}" ]]; then
  printf '\033[1;33m%s\033[0m\n' \
    "  Log out and back in (or run 'newgrp docker') before using docker without sudo."
fi
