#!/usr/bin/env bash
# =============================================================================
# PromoEngine - EC2 deployment script
#
# Runs ON the EC2 host, invoked over SSH by .github/workflows/deploy.yml after
# the new images have been pushed to Docker Hub. It can also be run by hand for
# the very first deployment or for a rollback.
#
#   cd /opt/promoengine && ./deploy.sh
#
# The one rule this script exists to enforce: the PostgreSQL volume is never
# touched. It uses `up -d` (which recreates only changed containers) and prunes
# images, never volumes. `down -v` appears nowhere.
# =============================================================================
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/opt/promoengine}"
ENV_FILE="${APP_DIR}/.env"

# Two inputs, merged in this order so a GitHub secret always beats a committed
# value: the repository's own .env (non-secret tunables only), then the overlay
# the pipeline built from the seven GitHub secrets.
ENV_FROM_REPO="${APP_DIR}/env.repo"
ENV_FROM_CI="${APP_DIR}/env.ci"

COMPOSE_FILE="${APP_DIR}/docker-compose.yml"
POSTGRES_VOLUME="promoengine-postgres-data"

cd "${APP_DIR}"

log()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m[warn] %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31m[fail] %s\033[0m\n' "$*" >&2; exit 1; }

# Docker Compose v2 is a docker subcommand; v1 was a separate binary. Support
# both so the script does not depend on which one the AMI happens to carry.
if docker compose version >/dev/null 2>&1; then
  COMPOSE="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE="docker-compose"
else
  die "Neither 'docker compose' nor 'docker-compose' is installed."
fi

[[ -f "${COMPOSE_FILE}" ]] || die "Missing ${COMPOSE_FILE}. Copy it from the repository first."

# -----------------------------------------------------------------------------
# 1. Build .env
#
# Three layers, lowest precedence first:
#   1. env.repo - the .env committed in the repository: role name, tenant
#      prefix, port, log level. No credentials.
#   2. env.ci   - built by the pipeline from the seven GitHub secrets.
#   3. generated - the JWT signing key and external API key, created once on
#      this host and then preserved. Rotating the signing key on every push
#      would sign every user out on every push.
# -----------------------------------------------------------------------------
log "Assembling environment file"

touch "${ENV_FILE}"
chmod 600 "${ENV_FILE}"

# Writes KEY=VALUE into .env, replacing any existing line for KEY. Values are
# written verbatim and never echoed, so nothing lands in the deployment log.
upsert_env() {
  local key="$1" value="$2" tmp
  tmp="$(mktemp)"
  grep -v -E "^${key}=" "${ENV_FILE}" > "${tmp}" 2>/dev/null || true
  printf '%s=%s\n' "${key}" "${value}" >> "${tmp}"
  mv "${tmp}" "${ENV_FILE}"
  chmod 600 "${ENV_FILE}"
}

read_env() {
  grep -E "^$1=" "${ENV_FILE}" 2>/dev/null | tail -n 1 | cut -d= -f2- || true
}

# Sets KEY to a freshly generated secret only if it has no value yet.
ensure_generated() {
  local key="$1"
  if [[ -z "$(read_env "${key}")" ]]; then
    upsert_env "${key}" "$(openssl rand -base64 48 | tr -d '\n=+/' | cut -c1-64)"
    warn "${key} was empty; generated a new value and stored it in ${ENV_FILE}."
    warn "Read it back with: sudo grep ^${key}= ${ENV_FILE}"
  fi
}

# Copies every KEY=VALUE from a delivered file into .env. Comment and blank
# lines are skipped, and so is any key with an empty value - an empty value
# means "not configured", and blanking what the host already has would break a
# running deployment rather than leave it alone.
merge_env_file() {
  local source_file="$1" line key value
  [[ -f "${source_file}" ]] || return 0

  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%$'\r'}"                      # tolerate CRLF
    [[ -z "${line}" || "${line}" == \#* ]] && continue
    [[ "${line}" != *=* ]] && continue
    key="${line%%=*}"
    value="${line#*=}"
    [[ -z "${value}" ]] && continue
    upsert_env "${key}" "${value}"
  done < "${source_file}"

  rm -f "${source_file}"
}

# Order matters: the committed configuration is laid down first, then the
# GitHub secrets overwrite anything they also define.
merge_env_file "${ENV_FROM_REPO}"
merge_env_file "${ENV_FROM_CI}"

ensure_generated JWT_SIGNING_KEY
ensure_generated EXTERNAL_API_KEY

# Defaults for anything still unset, so a hand-run first deploy works.
# Written as a loop rather than `[[ ... ]] && ...` one-liners: under `set -e` a
# false test at the end of an && list exits the script.
default_env() {
  if [[ -z "$(read_env "$1")" ]]; then
    upsert_env "$1" "$2"
  fi
}

# POSTGRES_DB is deliberately absent: it is a required secret, and defaulting
# it would quietly create a catalog under a different name than intended if the
# secret ever failed to reach this host. The check below fails loudly instead.
default_env POSTGRES_USER           "postgres"
default_env IMAGE_TAG               "latest"
default_env TENANT_DB_PREFIX        "PromoEngine_Tenant_"
default_env TENANT_SEED_SAMPLE_DATA "true"
default_env HTTP_PORT               "80"
default_env LOG_LEVEL               "Information"

# These three arrive from GitHub secrets. The pipeline already refuses to reach
# this host with any of them empty, so this is the guard for a hand-run deploy.
[[ -n "$(read_env POSTGRES_PASSWORD)" ]] \
  || die "POSTGRES_PASSWORD is not set. It comes from the GitHub secret of the same name."
[[ -n "$(read_env POSTGRES_DB)" ]] \
  || die "POSTGRES_DB is not set. It comes from the GitHub secret of the same name."
[[ -n "$(read_env DOCKER_USERNAME)" ]] \
  || die "DOCKER_USERNAME is not set. It comes from the GitHub secret of the same name."

# -----------------------------------------------------------------------------
# 2. Report what is about to run
#
# IMAGE_TAG is the commit SHA the pipeline just built. To roll back, set it to
# an older SHA in .env and re-run this script - the images for every previous
# commit are still in Docker Hub.
# -----------------------------------------------------------------------------
DEPLOY_TAG="$(read_env IMAGE_TAG)"
log "Deploying tag ${DEPLOY_TAG}"

if docker volume inspect "${POSTGRES_VOLUME}" >/dev/null 2>&1; then
  VOLUME_PREEXISTED=1
  log "PostgreSQL volume ${POSTGRES_VOLUME} exists - tenant databases will be preserved"
else
  VOLUME_PREEXISTED=0
  warn "PostgreSQL volume ${POSTGRES_VOLUME} does not exist yet; it will be created empty."
fi

# -----------------------------------------------------------------------------
# 3. Pull and start
#
# `up -d` recreates only the containers whose image or configuration changed.
# Postgres keeps running untouched when only application images moved, so a
# deploy does not interrupt in-flight tenant provisioning.
# -----------------------------------------------------------------------------
log "Pulling images"
${COMPOSE} pull --quiet backend frontend postgres

log "Starting stack"
${COMPOSE} up -d --remove-orphans

# -----------------------------------------------------------------------------
# 4. Verify
#
# The backend health endpoint only answers once the catalog database is open,
# so a pass here proves frontend, backend and PostgreSQL are all wired up.
# -----------------------------------------------------------------------------
log "Waiting for the backend to report healthy"

HEALTH_URL="http://localhost:$(read_env HTTP_PORT)/health"
deadline=$(( SECONDS + 180 ))
healthy=0

while (( SECONDS < deadline )); do
  if curl --fail --silent --max-time 5 "${HEALTH_URL}" >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 5
done

if (( healthy == 0 )); then
  warn "Health check did not pass within 180s. Recent backend logs:"
  ${COMPOSE} logs --tail 80 backend || true
  ${COMPOSE} ps || true

  # By far the most common cause on a host that already had a database: the
  # password in .env was changed after the volume was initialised. PostgreSQL
  # keeps the password it was created with, so the two stop matching.
  if (( VOLUME_PREEXISTED == 1 )) \
     && ${COMPOSE} logs --tail 200 backend 2>/dev/null \
        | grep -qi "password authentication failed\|28P01"; then
    warn "The backend is being refused by PostgreSQL on password authentication."
    warn "POSTGRES_PASSWORD applies only when the data directory is first created,"
    warn "so changing it in .env does not change the password inside an existing"
    warn "volume. Set the server password to match, from the host:"
    warn ""
    warn "  docker compose exec postgres psql -U postgres -c \\"
    warn "    \"ALTER USER postgres WITH PASSWORD '<the value now in .env>';\""
    warn ""
    warn "Then rewrite the stored tenant connection strings - see DEPLOYMENT.md section 3."
  fi

  die "Deployment verification failed. The volume is intact; fix and re-run."
fi

log "Health check passed"
curl --silent "${HEALTH_URL}"; echo

# -----------------------------------------------------------------------------
# 5. Reclaim disk
#
# Only dangling images - layers no longer referenced by any tag. Volumes are
# never pruned, so this can never reach the tenant databases.
# -----------------------------------------------------------------------------
log "Pruning unused images"
docker image prune --force --filter "until=24h" || true
docker container prune --force --filter "until=24h" || true

log "Deployment complete"
${COMPOSE} ps
