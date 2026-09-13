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
#
# Since the monitoring stack was added there are two compose files. The second
# is an overlay applied on top of the first, and whether it is applied at all is
# decided by MONITORING_ENABLED in .env:
#
#   docker compose -f docker-compose.yml -f docker-compose.monitoring.yml ...
#
# Everything below goes through the `compose` wrapper function so that the two
# can never drift apart - in particular so that `up -d --remove-orphans` is never
# run with only one of them, which would delete the other's containers.
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
MONITORING_COMPOSE_FILE="${APP_DIR}/docker-compose.monitoring.yml"
MONITORING_DIR="${APP_DIR}/monitoring"
POSTGRES_VOLUME="promoengine-postgres-data"

# Services in each file, listed explicitly so `pull` can name them and so a typo
# in a service name fails here rather than half way through a deployment.
APP_SERVICES=(postgres backend frontend)
MONITORING_SERVICES=(prometheus grafana loki alloy node-exporter cadvisor)

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
# Keys that arrived from the repository .env or the pipeline ON THIS RUN.
#
# Needed because .env on the host is cumulative: a value deploy.sh derived last
# time is indistinguishable, by the time it is read back, from one an operator
# set deliberately. This list is how a derived value can be recomputed every
# deploy while still yielding to an explicit choice.
DELIVERED_KEYS=" "

was_delivered() { [[ "${DELIVERED_KEYS}" == *" $1 "* ]]; }

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
    DELIVERED_KEYS="${DELIVERED_KEYS}${key} "
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

# -- monitoring -------------------------------------------------------------
# Defaults here rather than only in the repository's .env, so a host that was
# deployed before monitoring existed picks up sane values on its next deploy
# instead of failing on an unset variable.
default_env MONITORING_ENABLED         "true"
default_env GRAFANA_BIND_ADDRESS       "127.0.0.1"
default_env GRAFANA_PORT               "3000"
default_env GRAFANA_COOKIE_SECURE      "false"
default_env GRAFANA_LOG_LEVEL          "warn"
default_env PROMETHEUS_RETENTION_TIME  "15d"
default_env PROMETHEUS_RETENTION_SIZE  "4GB"
default_env PROMETHEUS_MEM_LIMIT       "512m"
default_env GRAFANA_MEM_LIMIT          "384m"
default_env LOKI_MEM_LIMIT             "384m"
default_env ALLOY_MEM_LIMIT            "256m"
default_env NODE_EXPORTER_MEM_LIMIT    "128m"
default_env CADVISOR_MEM_LIMIT         "256m"
default_env MONITORING_ENVIRONMENT     "production"
default_env MONITORING_HOSTNAME        "$(hostname -s 2>/dev/null || echo ec2)"

# The Grafana login is NOT generated here. It comes from the GRAFANA_USER and
# GRAFANA_PASSWORD GitHub secrets through env.ci, is required whenever monitoring
# is on (checked below), and is pushed into Grafana after every deploy by
# sync_grafana_admin - see section 4b.

# Where Grafana believes it is reachable. Only used for the links inside alert
# notifications, but a wrong value there sends people to a dead URL during an
# incident, which is the worst possible moment.
#
# Derived on EVERY deploy, not just when unset. GRAFANA_BIND_ADDRESS can change
# between deploys, and a root URL left over from when Grafana was on loopback
# would keep pointing at localhost after it moved to the public interface - a
# stale value that nothing would ever correct.
#
# An explicit GRAFANA_ROOT_URL in the repository .env, or as a secret, still
# wins: was_delivered says whether it arrived this run.
if ! was_delivered GRAFANA_ROOT_URL; then
  grafana_port="$(read_env GRAFANA_PORT)"
  if [[ "$(read_env GRAFANA_BIND_ADDRESS)" == "127.0.0.1" ]]; then
    # Loopback-only: the only way anyone reaches it is an SSH tunnel, so the
    # URL that works for them is localhost.
    upsert_env GRAFANA_ROOT_URL "http://localhost:${grafana_port}"
  else
    upsert_env GRAFANA_ROOT_URL "$(read_env PUBLIC_ORIGIN):${grafana_port}"
  fi
fi

# These three arrive from GitHub secrets. The pipeline already refuses to reach
# this host with any of them empty, so this is the guard for a hand-run deploy.
[[ -n "$(read_env POSTGRES_PASSWORD)" ]] \
  || die "POSTGRES_PASSWORD is not set. It comes from the GitHub secret of the same name."
[[ -n "$(read_env POSTGRES_DB)" ]] \
  || die "POSTGRES_DB is not set. It comes from the GitHub secret of the same name."
[[ -n "$(read_env DOCKER_USERNAME)" ]] \
  || die "DOCKER_USERNAME is not set. It comes from the GitHub secret of the same name."

# -----------------------------------------------------------------------------
# 1b. Decide whether the monitoring overlay is part of this deployment
#
# Both compose files are passed to every single docker compose invocation below,
# through the `compose` wrapper. That matters most for `up -d --remove-orphans`:
# run with only the application file, --remove-orphans would consider the six
# monitoring containers orphans of this project and delete them.
#
# Which is exactly the behaviour that makes MONITORING_ENABLED=false work. Turn
# it off, deploy, and the monitoring containers are removed on the next `up`.
# Their VOLUMES are left alone, so turning it back on restores the history.
# -----------------------------------------------------------------------------
COMPOSE_ARGS=(-f "${COMPOSE_FILE}")
MONITORING_ON=0

if [[ "$(read_env MONITORING_ENABLED)" == "true" ]]; then
  if [[ -f "${MONITORING_COMPOSE_FILE}" && -d "${MONITORING_DIR}" ]]; then
    COMPOSE_ARGS+=(-f "${MONITORING_COMPOSE_FILE}")
    MONITORING_ON=1

    # Required rather than defaulted. The pipeline already refuses to run
    # without them; this is the guard for a hand-run deploy.
    [[ -n "$(read_env GRAFANA_ADMIN_USER)" ]] \
      || die "GRAFANA_ADMIN_USER is not set. It comes from the GRAFANA_USER GitHub secret."
    [[ -n "$(read_env GRAFANA_ADMIN_PASSWORD)" ]] \
      || die "GRAFANA_ADMIN_PASSWORD is not set. It comes from the GRAFANA_PASSWORD GitHub secret."
  else
    warn "MONITORING_ENABLED=true but ${MONITORING_COMPOSE_FILE} or ${MONITORING_DIR} is missing."
    warn "Deploying the application without monitoring. The pipeline copies both;"
    warn "if you are running this by hand, copy them across too."
  fi
fi

# Every docker compose call in this script goes through here.
compose() { ${COMPOSE} "${COMPOSE_ARGS[@]}" "$@"; }

# -- optional alert notifications -------------------------------------------
# A Slack/Teams webhook URL is a credential and is never committed. If the
# secret is set, render the provisioning file on the host; if it is not, remove
# any previously rendered copy so that clearing the secret really does turn
# notifications off. Alert RULES are unaffected either way - they fire and show
# in Grafana regardless of whether anywhere is configured to receive them.
CONTACT_POINTS="${MONITORING_DIR}/grafana/provisioning/alerting/contact-points.yml"
if (( MONITORING_ON == 1 )); then
  if [[ -n "$(read_env GRAFANA_ALERT_WEBHOOK_URL)" ]]; then
    if [[ -f "${CONTACT_POINTS}.example" ]]; then
      # The file keeps the literal $GRAFANA_ALERT_WEBHOOK_URL placeholder:
      # Grafana expands it from the container's environment at read time, so the
      # URL itself never lands on disk here.
      grep -v '^#' "${CONTACT_POINTS}.example" > "${CONTACT_POINTS}"
      chmod 640 "${CONTACT_POINTS}"
      log "Alert notifications enabled (webhook URL supplied by secret)"
    fi
  elif [[ -f "${CONTACT_POINTS}" ]]; then
    rm -f "${CONTACT_POINTS}"
    warn "GRAFANA_ALERT_WEBHOOK_URL is no longer set; removed the rendered contact point."
    warn "Alert rules still evaluate and show in Grafana, but nothing is notified."
  fi
fi

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
PULL_SERVICES=("${APP_SERVICES[@]}")
if (( MONITORING_ON == 1 )); then
  PULL_SERVICES+=("${MONITORING_SERVICES[@]}")
  log "Monitoring overlay is enabled - ${#MONITORING_SERVICES[@]} extra services"
else
  log "Monitoring overlay is disabled (MONITORING_ENABLED != true)"
fi

# A pull failure on a monitoring image must not stop the application deploying,
# so those are pulled separately and only warned about. The application images
# are still mandatory.
compose pull --quiet "${APP_SERVICES[@]}"

if (( MONITORING_ON == 1 )); then
  if ! compose pull --quiet "${MONITORING_SERVICES[@]}"; then
    warn "One or more monitoring images could not be pulled."
    warn "Continuing: whatever is already on this host will be used instead."
  fi
fi

log "Starting stack"
compose up -d --remove-orphans

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
  compose logs --tail 80 backend || true
  compose ps || true

  # By far the most common cause on a host that already had a database: the
  # password in .env was changed after the volume was initialised. PostgreSQL
  # keeps the password it was created with, so the two stop matching.
  if (( VOLUME_PREEXISTED == 1 )) \
     && compose logs --tail 200 backend 2>/dev/null \
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
# 4b. Verify the observability pipeline end to end
#
# Three questions, all asked from inside the Prometheus container:
#   1. Is Grafana serving?
#   2. Does Prometheus consider the backend target up - i.e. did metrics arrive?
#   3. Has Loki actually received log lines - i.e. is Alloy shipping?
#
# NONE of this can fail the deployment. A broken dashboard is not a reason to
# roll back a working application, and a monitoring stack that can take
# production down is worse than no monitoring at all. Failures here print a
# warning and set MONITORING_DEGRADED, which the GitHub Actions workflow turns
# into a build annotation so it is visible without being fatal.
# -----------------------------------------------------------------------------
MONITORING_DEGRADED=0

if (( MONITORING_ON == 1 )); then
  log "Verifying the monitoring stack"

  # Every HTTP probe below runs from inside the PROMETHEUS container, not from
  # the container being tested. Two reasons, both practical:
  #
  #   1. It is the one image in this stack certain to carry an HTTP client - the
  #      -busybox tag was chosen for exactly that. Several of the others are
  #      distroless or minimal, and a probe that cannot run looks identical to a
  #      probe that failed.
  #   2. Reaching grafana:3000 and loki:3100 by service name also proves the
  #      monitoring network resolves, which testing each container against its
  #      own localhost would not.
  #
  # -T because an SSH-driven deploy has no TTY.
  probe() { compose exec -T prometheus wget -qO- --timeout=5 "$1" 2>/dev/null; }

  # Waits up to `timeout` seconds for a command to succeed AND for its output to
  # match a pattern. Retrying is not optional here: Prometheus needs a scrape
  # interval to pass before any target is up, and Loki's ingester takes a while
  # to join its own ring after a restart.
  wait_for() {
    local label="$1" timeout="$2" pattern="$3"; shift 3
    local deadline=$(( SECONDS + timeout )) output=""
    while (( SECONDS < deadline )); do
      if output="$("$@" 2>/dev/null)" && [[ "${output}" == *"${pattern}"* ]]; then
        printf '  [ ok ] %s\n' "${label}"
        return 0
      fi
      sleep 5
    done
    printf '\033[1;33m  [warn] %s\033[0m\n' "${label}"
    MONITORING_DEGRADED=1
    return 1
  }

  # 1. Grafana is serving. wget already fails on a non-2xx response, and an
  #    unhealthy Grafana answers /api/health with 503, so reaching this at all
  #    means the store behind users, dashboards and alert state opened.
  wait_for "Grafana is serving" 120 'database' \
    probe 'http://grafana:3000/api/health' \
    || warn "Grafana did not answer /api/health. Try: compose logs grafana"

  # 1b. Make the admin login match the GitHub secrets.
  #
  # GF_SECURITY_ADMIN_USER and GF_SECURITY_ADMIN_PASSWORD are read ONCE, when
  # Grafana first creates its database in the grafana-data volume. After that
  # they are ignored - so without this step, changing either secret would change
  # nothing, and the login would still be whatever it was on day one.
  #
  # Order matters, and so does doing as little as possible:
  #   1. If the secrets already log in, stop. Resetting a password that is
  #      already correct would sign every open session out on every deploy.
  #   2. Otherwise reset admin user 1's password with grafana cli. That needs no
  #      credentials, so it works whatever the password was before.
  #   3. If the login NAME still does not match, rename user 1 through the API,
  #      authenticating by its email, since the old login name is unknown.
  #
  # Credentials never appear on a command line: the CLI reads the password from
  # stdin, and curl reads its user:password from a config file on stdin. Nothing
  # here is printed.
  sync_grafana_admin() {
    local user pw bind port base code current body esc_pw

    user="$(read_env GRAFANA_ADMIN_USER)"
    pw="$(read_env GRAFANA_ADMIN_PASSWORD)"
    port="$(read_env GRAFANA_PORT)"
    bind="$(read_env GRAFANA_BIND_ADDRESS)"
    [[ "${bind}" == "0.0.0.0" || -z "${bind}" ]] && bind="127.0.0.1"
    base="http://${bind}:${port}"

    # curl config quoting: backslash and double quote are the only special
    # characters inside a quoted value.
    esc_pw=${pw//\\/\\\\}
    esc_pw=${esc_pw//\"/\\\"}

    # $1 = login or email to authenticate as; the rest are curl arguments.
    grafana_api() {
      local who="$1"; shift
      printf 'user = "%s:%s"\n' "${who}" "${esc_pw}" \
        | curl -K - --silent --max-time 10 "$@"
    }

    code="$(grafana_api "${user}" -o /dev/null -w '%{http_code}' "${base}/api/user" || true)"
    if [[ "${code}" == "200" ]]; then
      printf '  [ ok ] Grafana login already matches the GRAFANA_USER / GRAFANA_PASSWORD secrets\n'
      return 0
    fi

    if ! printf '%s\n' "${pw}" \
         | compose exec -T grafana grafana cli admin reset-admin-password --password-from-stdin \
           > /dev/null 2>&1; then
      warn "Could not reset the Grafana admin password. Grafana's password policy may reject it."
      return 1
    fi

    code="$(grafana_api "${user}" -o /dev/null -w '%{http_code}' "${base}/api/user" || true)"
    if [[ "${code}" == "200" ]]; then
      printf '  [ ok ] Grafana admin password updated from GRAFANA_PASSWORD\n'
      return 0
    fi

    # The password is right but the login name is not. Grafana's basic auth
    # accepts an email in place of a login, and the built-in admin's email is
    # admin@localhost unless someone changed it in the UI.
    current="$(grafana_api "admin@localhost" --fail "${base}/api/users/1" || true)"
    if [[ -z "${current}" ]]; then
      warn "Could not rename the Grafana admin to the GRAFANA_USER value."
      warn "The password is set, but the admin's email is no longer admin@localhost,"
      warn "so its current login could not be found. Log in with the old username."
      return 1
    fi

    body="$(python3 -c '
import json, sys
u = json.loads(sys.argv[2])
new = sys.argv[1]
name = u.get("name") or ""
print(json.dumps({
    "login": new,
    "email": u.get("email") or "admin@localhost",
    # Keep a real display name; replace only the default one.
    "name": new if name in ("", "admin", u.get("login")) else name,
}))' "${user}" "${current}")"

    code="$(grafana_api "admin@localhost" -o /dev/null -w '%{http_code}' \
              -X PUT -H 'Content-Type: application/json' --data "${body}" \
              "${base}/api/users/1" || true)"
    if [[ "${code}" != "200" ]]; then
      warn "Grafana refused to rename the admin user (HTTP ${code})."
      return 1
    fi

    code="$(grafana_api "${user}" -o /dev/null -w '%{http_code}' "${base}/api/user" || true)"
    if [[ "${code}" == "200" ]]; then
      printf '  [ ok ] Grafana admin login updated from GRAFANA_USER and GRAFANA_PASSWORD\n'
      return 0
    fi
    warn "Grafana admin was updated but the new login still does not authenticate (HTTP ${code})."
    return 1
  }

  sync_grafana_admin || MONITORING_DEGRADED=1

  # 2. Metrics. `up == 1` for the backend job is the single best proof that the
  #    application is exporting metrics AND that Prometheus can route to it
  #    across the promoengine network. An empty result set contains no "value"
  #    key at all, which is what makes this a meaningful pattern rather than a
  #    test that Prometheus merely answered.
  wait_for "Prometheus is scraping the backend" 150 '"value"' \
    probe 'http://localhost:9090/api/v1/query?query=up%7Bjob%3D%22promoengine-backend%22%7D%3D%3D1' \
    || warn "Prometheus has no healthy backend target. Check that the API image includes /metrics."

  # 3. Logs. Asking Loki which values it has seen for the `service` label proves
  #    the whole Docker -> Alloy -> Loki path, not merely that Loki is running.
  #    Looking for "backend" specifically, because Loki answering with an empty
  #    list is exactly the failure this is meant to catch.
  wait_for "Loki has received container logs" 150 'backend' \
    probe 'http://loki:3100/loki/api/v1/label/service/values' \
    || warn "Loki has no logs labelled with a backend service yet. Check: compose logs alloy"

  if (( MONITORING_DEGRADED == 1 )); then
    warn ""
    warn "The application deployed successfully; only monitoring is degraded."
    warn "Inspect with:"
    warn "  cd ${APP_DIR} && docker compose -f docker-compose.yml -f docker-compose.monitoring.yml ps"
  else
    log "Monitoring verified: metrics and logs are both reaching Grafana"
    printf '  Grafana: %s (user %s)\n' "$(read_env GRAFANA_ROOT_URL)" "$(read_env GRAFANA_ADMIN_USER)"
    if [[ "$(read_env GRAFANA_BIND_ADDRESS)" == "127.0.0.1" ]]; then
      printf '  Bound to loopback. Reach it with:\n'
      printf '    ssh -i <key>.pem -L %s:localhost:%s %s@<this-host>\n' \
        "$(read_env GRAFANA_PORT)" "$(read_env GRAFANA_PORT)" "$(id -un)"
    fi
  fi
fi

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
compose ps

# Exit 0 either way: the application is up, which is what this script promises.
# The marker file is how the workflow reports degraded monitoring as a warning
# annotation rather than a failed build.
if (( MONITORING_DEGRADED == 1 )); then
  printf 'degraded\n' > "${APP_DIR}/.monitoring-status"
else
  rm -f "${APP_DIR}/.monitoring-status"
fi
