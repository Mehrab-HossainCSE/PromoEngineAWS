# PromoEngine — monitoring and observability

Metrics, logs, dashboards and alerts for the EC2 deployment, using Prometheus,
Grafana, Loki, Grafana Alloy, Node Exporter and cAdvisor.

Everything here is an **overlay** on the existing deployment. The application's
three containers, its database volume and its multi-tenant behaviour are
untouched, and monitoring can be switched off with a single environment variable.

---

## 1. What this gives you

| Question | Where to look |
|---|---|
| Is the instance running out of CPU, memory or disk? | **EC2 Host** dashboard |
| Which container is eating the box? | **Docker Containers** dashboard |
| Is the API slow, and on which endpoint? | **Backend API** dashboard |
| What did the backend actually print when it broke? | **Logs** dashboard |
| Did anything go wrong while I was asleep? | Grafana → Alerting → Alert rules |

---

## 2. Architecture

Two independent pipelines, joined only at Grafana.

```
                      ┌──────────────────────────────────────────┐
  METRICS  (pull)     │                                          │
                      │   backend:8080/metrics ───┐              │
                      │   node-exporter:9100 ─────┼──► Prometheus│
                      │   cadvisor:8080 ──────────┘        │     │
                      │                                    ▼     │
                      │                                 Grafana  │
  LOGS  (push)        │                                    ▲     │
                      │   Docker socket ──► Alloy ──► Loki ┘     │
                      │                                          │
                      └──────────────────────────────────────────┘
```

**Metrics are pulled.** Prometheus opens an HTTP connection to each target every
15 seconds and reads a text document of numbers. Nothing is sent to it, so a
target that dies simply stops answering — which is why Prometheus can tell the
difference between "quiet" and "gone", and why the `up` metric exists at all.

**Logs are pushed.** Alloy asks the Docker daemon which containers exist, streams
their stdout and stderr, attaches labels, and posts batches to Loki.

### Why these six

| Component | Job | Why not something else |
|---|---|---|
| **Prometheus** | Stores metrics, evaluates nothing else | The de facto standard for pull-based metrics; Grafana speaks it natively |
| **Grafana** | Dashboards, and the alert engine | Its embedded Alertmanager means no seventh container |
| **Loki** | Stores logs | Indexes *labels*, not log text, so it fits on a small instance where Elasticsearch would not |
| **Alloy** | Collects Docker logs | Successor to Promtail; reads the Docker API, so it gets Compose labels from the daemon instead of parsing file paths |
| **Node Exporter** | Host CPU/memory/disk | cAdvisor only sees containers and cannot tell you the instance is too small |
| **cAdvisor** | Per-container resource use | Node Exporter only sees the host and cannot tell you *which* container is at fault |

### What is exposed

Only Grafana, and by default only on loopback.

| Service | Port | Published to the host? |
|---|---|---|
| Grafana | 3000 | **Yes — `127.0.0.1` only** |
| Prometheus | 9090 | No |
| Loki | 3100 | No |
| Alloy | 12345 | No |
| Node Exporter | 9100 | No |
| cAdvisor | 8080 | No |

Prometheus has no authentication of any kind. Anyone who can reach port 9090 can
read every metric in the system, so it stays on the private Docker network.

---

## 3. Files

### Created

| Path | What it is |
|---|---|
| `docker-compose.monitoring.yml` | The six services, their volumes and the `monitoring` network |
| `monitoring/prometheus/prometheus.yml` | Scrape targets and cAdvisor cardinality filters |
| `monitoring/loki/loki-config.yml` | Loki single-binary config: TSDB v13, filesystem storage, 14-day retention |
| `monitoring/alloy/config.alloy` | Docker log discovery → labels → Loki |
| `monitoring/grafana/provisioning/datasources/datasources.yml` | Prometheus and Loki data sources, fixed uids |
| `monitoring/grafana/provisioning/dashboards/dashboards.yml` | File-based dashboard provider |
| `monitoring/grafana/provisioning/alerting/rules.yml` | 10 alert rules in 4 groups |
| `monitoring/grafana/provisioning/alerting/contact-points.yml.example` | Optional notification routing (inactive) |
| `monitoring/grafana/dashboards/host-ec2.json` | EC2 Host dashboard |
| `monitoring/grafana/dashboards/docker-containers.json` | Docker Containers dashboard |
| `monitoring/grafana/dashboards/backend-api.json` | Backend API dashboard |
| `monitoring/grafana/dashboards/logs.json` | Logs dashboard |
| `PromoBackend/src/PromoEngine.Api/Observability/MetricsExtensions.cs` | The `/metrics` endpoint |
| `MONITORING.md` | This file |

### Modified

| Path | Change |
|---|---|
| `PromoBackend/.../PromoEngine.Api.csproj` | Four OpenTelemetry packages |
| `PromoBackend/.../Program.cs` | Two lines: register the exporter, map `/metrics` |
| `docker-compose.yml` | Two `Metrics__*` environment variables on the backend |
| `deploy/deploy.sh` | Applies the overlay, generates the Grafana password, verifies the pipeline |
| `.github/workflows/deploy.yml` | New `validate-monitoring` job; copies `monitoring/`; reports status |
| `.env` | Monitoring tunables |
| `PromoFrontend/nginx/default.conf` | Explicitly refuses public `/metrics` |
| `.gitattributes` | LF for `*.alloy` and `*.example` |

**No business logic was changed.** The only backend edits are two lines in
`Program.cs` and one new file that reads meters ASP.NET Core already publishes.

---

## 4. Application metrics

Nothing is instrumented by hand. .NET 8 and later publish request and runtime
meters on their own; OpenTelemetry collects them and serves them at `/metrics`.

| Metric | Type | What it answers |
|---|---|---|
| `http_server_request_duration_seconds` | histogram | Request rate, status codes **and** latency — all three come from this one metric |
| `http_server_active_requests` | gauge | How many requests are in flight right now |
| `kestrel_active_connections` | gauge | Open TCP connections |
| `dotnet_gc_collections_total` | counter | GC pressure by generation |
| `dotnet_thread_pool_queue_length_total` | counter | Thread-pool starvation |
| `dotnet_process_memory_working_set_bytes` | gauge | Process memory, to compare against the container limit |
| `target_info` | gauge | Carries `service_version` = the deployed commit SHA |

Labels on the request metric: `http_request_method`, `http_response_status_code`,
`http_route`, `network_protocol_version`, `url_scheme`.

**The route label is the route *template*** — `/api/promotions/{id}`, never
`/api/promotions/42`. This matters more than it looks: a label whose value varies
per request creates a new time series per value, and that is the standard way to
make Prometheus fall over. The same discipline is why the Loki labels in
`config.alloy` are limited to container, service, project and stream.

Percentiles are derived at query time:

```promql
histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket[5m])))
```

`sum by (le)` is not decoration — the quantile has to be computed over merged
buckets, and dropping every other label is what merges them.

### Security of the endpoint

`/metrics` is anonymous, because a scraper cannot hold a tenant JWT. It is
protected by network placement instead:

- The backend's port 8080 is never published to the host.
- Nginx proxies `/api/` and `/health`, and now explicitly returns 404 for
  `/metrics`.
- Prometheus reaches it as `backend:8080` over the private Docker network.

Verify from outside the instance — this must return 404:

```bash
curl -i http://<ec2-ip>/metrics
```

---

## 5. Dashboards

All four are provisioned from files and are **read-only in the UI**. The file in
git is the source of truth, so a dashboard cannot be silently changed by someone
dragging a panel and then lost when the container is recreated.

To change one: edit it in Grafana, export the JSON with *Export for sharing
externally* **off**, and commit it over the file.

**EC2 Host** — CPU (split by mode, so `iowait` and `steal` are visible),
memory (based on `MemAvailable`, not the misleading `MemFree`), filesystem usage,
disk and network throughput, load average.

**Docker Containers** — per-container CPU, memory, network; a sortable table with
memory limits and restart counts; the running backend image tag.

**Backend API** — request rate, error rate, p50/p95/p99, active requests, status
code distribution, p95 by route, .NET GC and thread pool, and a sortable route
table.

**Logs** — volume by service, error lines by service, a live tail and a
pre-filtered error view, with `$service` and `$search` variables.

---

## 6. Alerts

Ten rules in four groups, all provisioned from
`monitoring/grafana/provisioning/alerting/rules.yml`.

| Rule | Fires when | For | Severity |
|---|---|---|---|
| EC2 host CPU is saturated | CPU > 85% | 10m | warning |
| EC2 host memory is nearly exhausted | Available memory < 15% | 10m | warning |
| EC2 root disk is filling up | Root filesystem > 85% | 10m | critical |
| A container is restarting repeatedly | > 2 restarts in 15m | 5m | critical |
| A container is close to its memory limit | > 90% of its limit | 10m | warning |
| Backend API is not being scraped | `up == 0` | 2m | critical |
| Backend is returning server errors | 5xx > 5% of responses | 5m | critical |
| Backend p95 latency is high | p95 > 1.5s | 10m | warning |
| A monitoring target is down | any collector `up == 0` | 5m | warning |
| No container logs are reaching Loki | 0 lines in 5m | 10m | warning |

Three deliberate choices worth knowing:

- **Container restarts alert at more than two, not more than zero.** A deploy
  legitimately restarts every application container exactly once. Paging on a
  successful deploy is the fastest way to teach people to ignore alerts.
- **Backend-down waits two minutes**, because a normal deploy briefly takes the
  container away.
- **The last rule queries Loki, not Prometheus.** It tests the data path end to
  end, so it catches the case where Alloy is running and healthy but shipping
  nothing — which a process-liveness check cannot.

### Turning on notifications

Alert rules fire and appear in Grafana whether or not anywhere is configured to
receive them. To have them delivered:

1. Create an incoming webhook in Slack, Teams, Discord or your on-call tool.
2. Add it as a GitHub repository secret named `GRAFANA_ALERT_WEBHOOK_URL`.
3. Push.

`deploy.sh` renders `contact-points.yml` on the host from that secret. A webhook
URL is a credential — anyone holding it can post into your channel — so it is
never committed. Removing the secret deletes the rendered file on the next
deploy.

---

## 7. Deploying

Nothing changes about how you deploy. Push to `main`.

```
push to main
   │
   ├─► build backend image ─────┐
   ├─► build frontend image ────┤
   ├─► validate monitoring ─────┤   (promtool, alloy fmt, dashboard/alert checks,
   │                            │    merged compose config)
   │                            ▼
   └──────────────────► deploy to EC2
                          ├─ copy docker-compose.yml, docker-compose.monitoring.yml,
                          │  deploy.sh, .env, monitoring/
                          ├─ deploy.sh: pull, up -d, health check
                          ├─ deploy.sh: verify metrics and logs reach Grafana
                          └─ report status
```

The `validate-monitoring` job runs in parallel with the image builds and gates
the deploy, so a typo in a dashboard cannot reach production. It runs the real
`promtool` and the real `alloy fmt`, checks every dashboard's JSON and data
source uid, checks every alert rule's condition refId, and merges both Compose
files.

### By hand

```bash
cd /opt/promoengine
docker compose -f docker-compose.yml -f docker-compose.monitoring.yml up -d
```

Both `-f` flags, always. Running `up -d --remove-orphans` with only the
application file would delete the six monitoring containers, because Compose
would consider them orphans of the project.

### Turning monitoring off

Set `MONITORING_ENABLED=false` in `.env` and deploy. The monitoring containers
are removed on the next `up`; **their volumes are left alone**, so turning it
back on restores the history.

---

## 8. Getting into Grafana

Grafana binds `127.0.0.1` by default, so it is not reachable from the internet.
This is the right default while the host has no TLS: published on `0.0.0.0`,
every Grafana login would cross the internet in clear text.

```bash
ssh -i your-key.pem -L 3000:localhost:3000 ubuntu@<ec2-public-ip>
```

Then open <http://localhost:3000>.

**Username:** `admin`

**Password:** generated on the host on the first deploy and preserved after that.
Read it back with:

```bash
sudo grep ^GRAFANA_ADMIN_PASSWORD= /opt/promoengine/.env
```

To set it yourself, add a `GRAFANA_ADMIN_PASSWORD` GitHub secret. Note that
Grafana, like PostgreSQL, only reads that on **first** start — afterwards the
password lives in Grafana's own database inside the `grafana-data` volume.
Changing it later needs:

```bash
docker compose -f docker-compose.yml -f docker-compose.monitoring.yml \
  exec grafana grafana cli admin reset-admin-password '<new password>'
```

### Exposing it directly instead

Only worth doing with TLS in front of it. If you must:

1. `GRAFANA_BIND_ADDRESS=0.0.0.0` in `.env`.
2. Add an inbound rule for port 3000 in the EC2 security group — **restricted to
   your own IP**, not `0.0.0.0/0`.
3. Set `GRAFANA_COOKIE_SECURE=true` once there is a certificate.

---

## 9. Validation

Run these on the instance to confirm each hop.

```bash
cd /opt/promoengine
C="docker compose -f docker-compose.yml -f docker-compose.monitoring.yml"

# All nine containers up
$C ps

# Every Prometheus target healthy
$C exec -T prometheus wget -qO- \
  'http://localhost:9090/api/v1/targets?state=active' \
  | grep -o '"job":"[^"]*","[^}]*"health":"[^"]*"'

# The application is exporting metrics
$C exec -T prometheus wget -qO- \
  'http://backend:8080/metrics' | grep -c '^http_server_request_duration'

# Logs have arrived, and from which containers
$C exec -T prometheus wget -qO- \
  'http://loki:3100/loki/api/v1/label/service/values'

# Grafana is serving
$C exec -T prometheus wget -qO- 'http://grafana:3000/api/health'

# /metrics is NOT public — this must print 404
curl -s -o /dev/null -w '%{http_code}\n' http://localhost/metrics
```

`deploy.sh` runs the equivalent of the last four on every deploy. **They never
fail the build** — a broken dashboard is not a reason to roll back a working
application. A failure writes `/opt/promoengine/.monitoring-status`, which the
workflow turns into a warning annotation.

### What was verified while building this

- The four OpenTelemetry packages restore and build clean against .NET 10
  (0 warnings, 0 errors) in the real project.
- The exported metric names and labels were captured from a running .NET 10
  process, not assumed — every dashboard query and alert expression was written
  against that output.
- `Metrics__Enabled=false` returns 404 on `/metrics` and leaves the application
  running normally; `Metrics__ServiceVersion` reaches `target_info`.
- All six image tags exist and publish both `amd64` and `arm64` manifests.
- `deploy.sh` was run end to end against stubbed Docker in four scenarios:
  monitoring on, monitoring on with a webhook, monitoring off, and probes
  failing. Correct compose files, correct rendered files, exit 0 in all four.
- The CI validator was negative-tested: it catches a bad datasource uid and a
  duplicate dashboard uid and fails the build.

Not verified, because there is no Docker daemon in this environment: the six
containers actually starting and talking to each other. That happens on the
first deploy.

---

## 10. Resource cost, and the one thing to check first

| | Approximate memory |
|---|---|
| Application (PostgreSQL, backend, Nginx) | ~500 MB |
| Monitoring stack | ~700–900 MB |
| **Total** | **~1.5 GB** |

**A `t2.micro` or `t3.micro` (1 GB) cannot run this.** The practical minimum is
`t3.small` (2 GB); `t3.medium` (4 GB) is comfortable. Check before deploying:

```bash
free -m
df -h /
```

Every monitoring container has a `mem_limit`. That is deliberate: if something
here grows unexpectedly, Docker kills the monitoring container rather than
leaving the kernel's OOM killer to choose a victim — and the kernel's usual
choice is the largest process, which is PostgreSQL.

Disk: Prometheus is capped at 15 days **or** 4 GB, whichever comes first; Loki
keeps 14 days. Both are tunable — Prometheus in `.env`, Loki in
`monitoring/loki/loki-config.yml`.

---

## 11. Manual steps required

Only one is mandatory.

1. **Confirm the instance has at least 2 GB of RAM.** See above. This is the only
   thing that will actually break if ignored.

Optional:

2. **`GRAFANA_ADMIN_PASSWORD` secret** — otherwise one is generated for you.
3. **`GRAFANA_ALERT_WEBHOOK_URL` secret** — otherwise alerts fire but are not
   delivered anywhere.
4. **Security group** — nothing to change. Grafana is on loopback; only open port
   3000 if you deliberately set `GRAFANA_BIND_ADDRESS=0.0.0.0`.

---

## 12. Troubleshooting

**Backend target is down in Prometheus.** Check `/metrics` is being served:
`$C exec -T backend curl -s localhost:8080/metrics | head`. If it 404s,
`Metrics__Enabled` is false or the image predates this change.

**No logs in the Logs dashboard.** `$C logs alloy`. The usual causes are the
Docker socket mount missing and Loki refusing pushes — Loki rejects log lines
whose timestamps are older than 168h, so a container with a badly wrong clock
goes silent.

**Grafana shows "Datasource not found".** A dashboard references a uid that is
not provisioned. The `validate-monitoring` job catches this before deploy; if you
edited a dashboard by hand on the host, that is the cause.

**cAdvisor reports nothing.** It needs `privileged: true` on cgroup v2 hosts
(Ubuntu 22.04+, Amazon Linux 2023) and reports partial stats rather than failing
loudly without it.

**Prometheus will not start.** It refuses to start on a bad config, unlike
Grafana which logs and carries on. `$C logs prometheus` names the line.

**Disk filling up.** Almost always old Docker images, not monitoring data:
`docker image prune -a --filter until=168h`. Never prune volumes — that is where
every tenant database lives.
