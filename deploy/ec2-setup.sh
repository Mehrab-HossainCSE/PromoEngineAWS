#!/usr/bin/env bash
# =============================================================================
# PromoEngine - one-time EC2 host preparation
#
# The deploy pipeline copies this script to the instance and runs it whenever
# the host is not ready, so it normally needs no manual invocation. It is also
# safe to run by hand - every step checks first, so a second run does nothing.
#
# It installs Docker, installs the Compose plugin, adds the login user to the
# docker group, creates the deployment directory and pre-creates the PostgreSQL
# volume and the Docker network so that the very first `compose up` has nothing
# left to discover.
#
#   curl -fsSL -o ec2-setup.sh <raw url>   # or scp it across
#   chmod +x ec2-setup.sh && ./ec2-setup.sh
#
# The one thing it cannot arrange for itself is passwordless sudo: installing
# packages needs root, and the pipeline cannot answer a password prompt.
#
# Supports Amazon Linux 2023 and Ubuntu 22.04/24.04.
#
# SIZING: with the monitoring stack enabled the instance needs at least 2 GB of
# RAM (the application is roughly 500 MB, monitoring another 700-900 MB). A
# t2.micro or t3.micro will not fit. This script reports the host's memory at
# the end so the mismatch is visible before the first deploy rather than as an
# OOM kill afterwards.
# =============================================================================
set -Eeuo pipefail

APP_DIR="/opt/promoengine"
POSTGRES_VOLUME="promoengine-postgres-data"
DOCKER_NETWORK="promoengine"

# `id -un` rather than $USER: this script is also run unattended over SSH by the
# deploy pipeline, and a non-interactive shell is precisely where $USER is least
# reliable. `id` asks the kernel and is always right.
RUN_USER="$(id -un)"

# Ubuntu's apt will otherwise stop on a config-file prompt or on needrestart's
# "which services should be restarted?" dialog, and an unattended run would hang
# there until the SSH connection times out.
export DEBIAN_FRONTEND=noninteractive
export NEEDRESTART_MODE=a
export NEEDRESTART_SUSPEND=1

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
if id -nG "${RUN_USER}" | tr ' ' '\n' | grep -qx docker; then
  log "${RUN_USER} is already in the docker group"
else
  log "Adding ${RUN_USER} to the docker group"
  sudo usermod -aG docker "${RUN_USER}"
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
sudo chown "${RUN_USER}:${RUN_USER}" "${APP_DIR}"
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
  Memory on this host  : $(free -m 2>/dev/null | awk '/^Mem:/{print $2" MB"}' || echo unknown)

  Next:
    1. Security group inbound: 22 from your IP, 80 from 0.0.0.0/0.
       Do NOT open 5432 - PostgreSQL is reachable only inside the Docker network.
       Nothing extra is needed for monitoring: Grafana binds 127.0.0.1 and is
       reached over an SSH tunnel. See MONITORING.md.
    2. Add the repository secrets, then push to main. The pipeline copies
       docker-compose.yml and deploy.sh here and starts the stack.
    3. Watch the first deploy with:
         cd ${APP_DIR} && docker compose logs -f backend

SUMMARY

if [[ -n "${NEEDS_RELOGIN:-}" ]]; then
  printf '\033[1;33m%s\033[0m\n' \
    "  Log out and back in (or run 'newgrp docker') before using docker without sudo."
fi
