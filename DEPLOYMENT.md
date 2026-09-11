# PromoEngine — Docker & AWS EC2 Deployment

Containerisation and CI/CD for the existing PromoEngine SaaS application. No
business logic and no part of the multi-tenant design was changed: the same
`LocalPostgresTenantProvisioner` that creates a database per tenant on a
developer machine creates it on the PostgreSQL container in production.

---

## 1. What was added

| Path | Purpose |
|---|---|
| [PromoBackend/Dockerfile](PromoBackend/Dockerfile) | Multi-stage .NET 10 build → ASP.NET runtime image |
| [PromoBackend/.dockerignore](PromoBackend/.dockerignore) | Keeps build output and local secrets out of the image |
| [PromoFrontend/Dockerfile](PromoFrontend/Dockerfile) | Multi-stage Angular 20 build → Nginx |
| [PromoFrontend/nginx/default.conf](PromoFrontend/nginx/default.conf) | SPA routing, `/api` reverse proxy, caching, security headers |
| [PromoFrontend/.dockerignore](PromoFrontend/.dockerignore) | Keeps `node_modules` and `dist` out of the build context |
| [docker-compose.yml](docker-compose.yml) | Production stack: frontend, backend, postgres |
| [.env](.env) | **Committed** database configuration and deployment tunables |
| [.github/workflows/deploy.yml](.github/workflows/deploy.yml) | Build → push → deploy on every push to `main` |
| [deploy/ec2-setup.sh](deploy/ec2-setup.sh) | One-time EC2 host preparation |
| [deploy/deploy.sh](deploy/deploy.sh) | Server-side deploy: pull, restart, verify, prune |

One existing file was modified, and it was a build-configuration fix rather than
a logic change:

**[PromoFrontend/angular.json](PromoFrontend/angular.json)** — the `production`
configuration had no `fileReplacements` block, so `environment.production.ts`
was dead code and *every* build, production included, baked in
`apiUrl: 'http://localhost:5193'`. In a container that address is the frontend
container itself, so the application could never have reached the API. The
production build now correctly substitutes `environment.production.ts`, whose
`apiUrl` is `''` — making every call in `ApiService` a same-origin
`/api/...` request that Nginx proxies to the backend.

---

## 2. Architecture

```
                              Internet
                                 │
                        :80 (security group)
                                 │
┌────────────────────────────────┼─────────────────────────────────────┐
│ AWS EC2                        │                                     │
│                                ▼                                     │
│  ┌──────────────────── docker network: promoengine ────────────────┐  │
│  │                                                                 │  │
│  │   frontend  (Nginx, promoengine-frontend)          :80          │  │
│  │      │  serves the compiled Angular bundle                      │  │
│  │      │  proxies /api/* and /health                              │  │
│  │      ▼                                                          │  │
│  │   backend   (.NET 10, promoengine-backend)         :8080        │  │
│  │      │  catalog DB: tenants, plans, subscriptions               │  │
│  │      │  provisioner: CREATE DATABASE per tenant                 │  │
│  │      ▼                                                          │  │
│  │   postgres  (postgres:16-alpine)                   :5432        │  │
│  │      │                                                          │  │
│  │      ├── PromoEngineCatalog          ← control plane            │  │
│  │      ├── PromoEngine_Tenant_jane_contoso_com                    │  │
│  │      ├── PromoEngine_Tenant_acme_corp_com                       │  │
│  │      └── …one database per tenant                               │  │
│  └──────────────────────────────┬──────────────────────────────────┘  │
│                                 │                                     │
│                    volume: promoengine-postgres-data                  │
│                    (survives every redeployment)                      │
└───────────────────────────────────────────────────────────────────────┘
```

**Only the frontend publishes a port.** The backend is reachable solely through
the Nginx proxy and PostgreSQL solely from inside the Docker network, so the
security group never needs a rule for 8080 or 5432.

**Nothing addresses anything as `localhost`.** Containers find each other by
Compose service name through Docker's embedded DNS: Nginx proxies to
`backend:8080`, the backend connects to `Host=postgres`. Inside a container
`localhost` is that container alone.

### Request path

1. Browser loads `http://<ec2-host>/` → Nginx returns `index.html` and the
   hashed Angular bundles.
2. Angular calls `/api/auth/login` — same origin, so **no CORS pre-flight and no
   cross-origin exposure of the API**.
3. Nginx matches `location /api/` and proxies to `http://backend:8080/api/...`,
   passing the `Authorization` header through.
4. `TenantResolutionMiddleware` reads the tenant from the JWT, loads that
   tenant's row from the catalog, and `TenantDbContextFactory` opens a context
   against that tenant's own database.

---

## 3. Database-per-tenant after deployment

The behaviour is unchanged; only the address of the PostgreSQL server moved.

### How it works

`Tenancy:Provisioner` stays `Local`. The name is historical — it means "create
databases directly on a PostgreSQL server with `CREATE DATABASE`" as opposed to
`Aws`, which drives the **RDS control-plane API** to create instances. With the
`postgres` container as its server, `Local` is exactly the right provisioner
here; the `Aws` provisioner would be wrong, because there is no RDS instance.

On registration:

1. `TenantOnboardingService` writes a `Tenant` row to the catalog and enqueues
   provisioning; `TenantProvisioningWorker` picks it up in the background.
2. `LocalPostgresTenantProvisioner` opens
   `Tenancy__Local__ServerConnectionString`, which points at the **`postgres`
   maintenance database** — PostgreSQL cannot run `CREATE DATABASE` from inside
   the database being created.
3. It checks `pg_database`, then issues
   `CREATE DATABASE "PromoEngine_Tenant_jane_contoso_com"`.
4. It takes that same connection string, swaps `Database=` for the new name, and
   hands the result to `TenantSchemaInitializer`, which creates the promotion
   schema and optionally seeds sample data.
5. The derived connection string is stored on the tenant row
   (`TenantProvisioningQueue` → `tenant.ConnectionString`).

Because the tenant string is *derived from* the server string, the credentials
and the `Host=postgres` hostname come along automatically. Nothing about tenant
databases is hardcoded anywhere: the prefix comes from `TENANT_DB_PREFIX`, the
credentials from `POSTGRES_USER`/`POSTGRES_PASSWORD`, and the host from the
Compose service name.

### Two constraints this creates

Tenant connection strings are **persisted**, so a stored string must stay valid:

> **Never rename the `postgres` service.** Every existing tenant row holds
> `Host=postgres`. Renaming the service breaks every tenant already provisioned
> while new ones keep working — a confusing half-failure.

> **`POSTGRES_PASSWORD` is only applied once, at `initdb`.** The PostgreSQL
> image reads it when it initialises an *empty* data directory and ignores it
> ever after. Change it in `.env` once the volume exists and the backend starts
> presenting a password the server no longer accepts — an authentication
> failure with no obvious cause. `deploy.sh` detects this case and prints the
> fix, which is to change the password inside the database as well:
>
> ```bash
> docker compose exec postgres psql -U postgres -c \
>   "ALTER USER postgres WITH PASSWORD '<the value now in .env>';"
> ```
>
> This is why the deploy script hard-stops on the placeholder password shipped
> in `.env`: getting it right before the first deploy avoids the whole problem.

> **Rotating `POSTGRES_PASSWORD` requires a catalog update.** The old password is
> baked into every stored tenant connection string. Change the password and
> tenant requests fail with authentication errors while the catalog itself
> (which reads the password from the environment) keeps working. After a
> rotation, rewrite the stored strings:
>
> ```sql
> -- connect to the catalog database
> UPDATE "Tenants"
> SET "ConnectionString" = regexp_replace(
>       "ConnectionString", 'Password=[^;]*', 'Password=<new-password>')
> WHERE "ConnectionString" IS NOT NULL;
> ```
>
> Then restart the backend. Plan a rotation as a short maintenance window.

### Migrating an existing catalog

If you restore a catalog dumped from a developer machine, its tenant rows say
`Host=localhost`. Rewrite them once, after restoring and before starting the
backend:

```sql
UPDATE "Tenants"
SET "ConnectionString" = replace("ConnectionString", 'Host=localhost', 'Host=postgres')
WHERE "ConnectionString" LIKE '%Host=localhost%';
```

Restore the tenant databases themselves into the same container, then let
`TenantSchemaSyncService` bring their schemas up to date on the next start.

### Data survives redeployment

Tenant databases are files inside the `promoengine-postgres-data` volume, which
is completely independent of container lifetime. A deploy runs
`docker compose pull` + `up -d`; Compose recreates only the containers whose
image changed, so the `postgres` container usually is not even restarted. The
pipeline prunes *images and containers* and never volumes — `down -v` appears
nowhere in this repository.

---

## 4. PostgreSQL permissions

`CREATE DATABASE` needs a specific set of rights. The default configuration
satisfies all of them because `POSTGRES_USER=postgres` is the cluster superuser
created by `initdb`.

| Requirement | Why the provisioner needs it | Superuser | Least-privilege role |
|---|---|---|---|
| Connect to the `postgres` database | `CREATE DATABASE` must be issued from another database | yes | granted by default |
| `CREATEDB` attribute | Issue `CREATE DATABASE` | implied | `CREATEDB` |
| Own the new database | Create tables in it via `EnsureCreated` | implied | automatic — the creator owns it |
| `CREATE` on schema `public` in the new database | `TenantSchemaInitializer` creates tables there | implied | automatic via `pg_database_owner` |
| Terminate other backends | `DROP DATABASE` on a rolled-back sign-up first calls `pg_terminate_backend` | implied | `pg_signal_backend` |

Two of these deserve a note:

- **PostgreSQL 15 changed the `public` schema default.** `PUBLIC` no longer has
  `CREATE` on it. This does not affect PromoEngine, because the role that runs
  `CREATE DATABASE` becomes the database owner and the `public` schema is owned
  by `pg_database_owner` — the owner can still create tables. It *would* bite if
  a different, non-owner role connected to the tenant database, which is why the
  application uses one role for both provisioning and tenant access.
- **`pg_terminate_backend`** is only reached on the deprovision path, when a
  sign-up is rolled back. A non-superuser can terminate its own sessions but
  needs `pg_signal_backend` to terminate anyone else's.

### Optional: run as a non-superuser

Superuser is the simplest correct answer for a single-tenant-per-database app
that owns its whole server, and it is what the configuration ships with. To
narrow it, create a dedicated role:

```sql
-- as postgres, once
CREATE ROLE promo_app WITH LOGIN PASSWORD '<strong-password>' CREATEDB;
GRANT pg_signal_backend TO promo_app;

-- let it own the catalog too
ALTER DATABASE "PromoEngineCatalog" OWNER TO promo_app;
```

Then set `POSTGRES_USER=promo_app` in `.env` and redeploy.

> Do this **before the first tenant is provisioned**, or rewrite the stored
> tenant connection strings the same way as for a password rotation — they carry
> `Username=` as well as `Password=`.

What this buys you: the role cannot read other clusters' files, create
extensions, or bypass row-level security. What it does not buy you: isolation
*between* tenants, since one role still reaches every tenant database. Real
per-tenant credentials would be a change to the provisioner, and therefore
outside the scope of this containerisation work.

---

## 5. Configuration: where every value comes from

Configuration arrives from two places, merged on the host in a defined order.

```
repository .env            five GitHub secrets
  (committed)                (never committed)
       │                            │
       │  scp as env.repo           │  scp as env.ci
       └────────────┬───────────────┘
                    ▼
        deploy.sh merges, secrets last
                    ▼
        /opt/promoengine/.env   (chmod 600)
                    ▼
        docker-compose.yml  ${VARIABLE} substitution
                    ▼
        container environment → .NET configuration
```

A GitHub secret always wins over a committed value of the same name, so a
setting can be overridden without editing the repository.

### The five GitHub secrets

| Secret | Used in | How |
|---|---|---|
| `DOCKER_USERNAME` | `deploy.yml` → `docker/login-action`, image tags, `env.ci` | `${{ secrets.DOCKER_USERNAME }}/promoengine-backend:…`; becomes `DOCKER_USERNAME` in the host `.env`, which `docker-compose.yml` reads to resolve `image:` |
| `DOCKER_PASSWORD` | `deploy.yml` → login on the runner (push) and on EC2 (pull) | `docker/login-action`; base64-piped into `docker login --password-stdin` over SSH |
| `AWS_HOST` | `deploy.yml` → `ssh-keyscan`, `scp`, `ssh`, public health check | also becomes `PUBLIC_ORIGIN` → `Cors__AllowedOrigins__0` |
| `AWS_USER` | `deploy.yml` → SSH login user | `ssh "$AWS_USER@$AWS_HOST"` |
| `AWS_KEY` | `deploy.yml` → written to `~/.ssh/deploy_key`, deleted afterwards | PEM private key for the EC2 key pair |

### The committed [.env](.env)

| Variable | Consumed by |
|---|---|
| `POSTGRES_USER` | postgres container; `Username=` in both connection strings |
| `POSTGRES_DB` | postgres container; `Database=` in `ConnectionStrings__Catalog` |
| `POSTGRES_PASSWORD` | postgres container; `Password=` in the catalog **and** the tenant-provisioning connection string |
| `TENANT_DB_PREFIX` | `Tenancy__DatabaseNamePrefix` — the `PromoEngine_Tenant_` prefix |
| `TENANT_SEED_SAMPLE_DATA` | `Tenancy__SeedSampleData` |
| `HTTP_PORT` | host port the frontend publishes |
| `LOG_LEVEL` | `Logging__LogLevel__Default` |

> ⚠️ **This file is committed, so everyone who can read the repository can read
> the database password.** That is an accepted trade-off for keeping GitHub
> secrets down to five, and it is safe only while PostgreSQL has no published
> port — which is how [docker-compose.yml](docker-compose.yml) ships it, so the
> password is not usable from outside the EC2 host. **If the repository is
> public, make it private**, or treat the password as disposable and rotate it
> (see §3 — a rotation also requires rewriting the stored tenant connection
> strings). To move it back into GitHub secrets later, delete the two lines
> from `.env` and add them to the `Assemble secret overlay` step; the merge
> skips empty values, so nothing else has to change.

### Two secrets that are generated rather than configured

`JWT_SIGNING_KEY` and `EXTERNAL_API_KEY` are not in your secret list, and both
are required in production — `appsettings.json` ships with placeholder values
that must not run in production. Rather than fail the first deploy,
[deploy/deploy.sh](deploy/deploy.sh) generates each one on first use, stores it
in `/opt/promoengine/.env`, and then **never touches it again** (regenerating the
JWT key on every push would sign every user out on every push).

Read a generated value back on the host with:

```bash
sudo grep '^EXTERNAL_API_KEY=' /opt/promoengine/.env
```

> If external systems already call `/api/external/...` with the key currently in
> `appsettings.json`, set that value in `.env` **before the first deploy** so the
> generator leaves it alone — otherwise those callers start getting 401s.

To manage either through GitHub instead, add it as a repository secret and one
line to the `Assemble secret overlay` step:

```yaml
echo "JWT_SIGNING_KEY=${{ secrets.JWT_SIGNING_KEY }}"
```

An empty value in the overlay is skipped by `deploy.sh` rather than blanking
what the host already has, so adding the secret later is safe.

### What stays out of the repository and the logs

- The five GitHub secrets are never committed and never echoed.
- `/opt/promoengine/.env` is assembled on the host, `chmod 600`.
- No secret is an argument to any command on the EC2 host, so none is visible in
  `ps`. The remote script arrives on `bash -s`'s stdin; the Docker Hub token is
  base64-encoded so no character in it can break out of shell quoting.
- No `ARG`/`ENV` in either Dockerfile carries a credential — configuration
  arrives at container start.
- `~/.ssh/deploy_key` and the overlay file are deleted in an `if: always()` step;
  `env.repo` and `env.ci` are deleted on the host once merged.
- `appsettings.Local.json`, `.env*` and `secrets.json` are in `.dockerignore`,
  so the committed `.env` never becomes an image layer either.

The one deliberate exception is `POSTGRES_USER`/`POSTGRES_DB`/`POSTGRES_PASSWORD`
in the committed [.env](.env) — see the warning in §5.

> **Password characters matter.** `POSTGRES_PASSWORD` is interpolated into an
> Npgsql connection string, where `;` and `=` are delimiters, and into a
> Compose `.env` file, where a leading `#` starts a comment. Use a long
> alphanumeric password: `openssl rand -base64 32 | tr -d '/+=' | cut -c1-32`.

---

## 6. AWS EC2 setup

### Instance

- **AMI**: Amazon Linux 2023 or Ubuntu 24.04 LTS
- **Type**: `t3.small` minimum. The .NET build happens on the GitHub runner, not
  here, but PostgreSQL plus two containers on `t3.micro`'s 1 GB is tight — and
  each tenant database adds background workers.
- **Storage**: 30 GB gp3. Tenant databases and image layers both live on it.
- **Key pair**: the private key becomes the `AWS_KEY` secret.

### Security group

| Direction | Port | Source | Reason |
|---|---|---|---|
| Inbound | 22 (SSH) | your IP, or GitHub Actions ranges | deployment and administration |
| Inbound | 80 (HTTP) | `0.0.0.0/0` | the application |
| Inbound | 443 (HTTPS) | `0.0.0.0/0` | once TLS is added (§8) |
| Outbound | all | `0.0.0.0/0` | pulling images from Docker Hub |

**Do not open 5432.** PostgreSQL has no published host port; it is reachable
only from inside the Docker network. To inspect it, tunnel over SSH:

```bash
ssh -L 5432:localhost:5432 ec2-user@<host>   # after uncommenting the ports
                                             # block in docker-compose.yml
```

### Host preparation — run once

```bash
ssh -i promo-key.pem ec2-user@<ec2-host>

# copy deploy/ec2-setup.sh across, or paste it, then:
chmod +x ec2-setup.sh
./ec2-setup.sh

# the script adds you to the docker group; the new membership needs a new session
exit && ssh -i promo-key.pem ec2-user@<ec2-host>
docker ps          # must work without sudo, or the pipeline cannot deploy
```

It installs Docker and the Compose v2 plugin, creates `/opt/promoengine`,
pre-creates the `promoengine-postgres-data` volume and the `promoengine`
network, and caps Docker's log growth.

### First deployment

Set `POSTGRES_PASSWORD` in the committed [.env](.env) first — it ships with a
placeholder, and the deploy refuses to run until it is a real value:

```bash
openssl rand -base64 32 | tr -d '/+=' | cut -c1-32
```

Then, with the five secrets configured:

```bash
git push origin main
```

The pipeline creates `/opt/promoengine/{docker-compose.yml,deploy.sh,.env}` and
starts the stack. On this first run PostgreSQL runs `initdb`, `CatalogSeeder`
creates the catalog schema and seeds the subscription plans, and the backend's
`start_period` of 90 s covers it.

To deploy by hand instead — a first run before the pipeline exists, or a
rollback — copy the repository's `.env` up and add the two values the pipeline
would otherwise inject:

```bash
scp .env deploy/deploy.sh docker-compose.yml <user>@<host>:/opt/promoengine/

ssh <user>@<host>
cd /opt/promoengine
chmod 600 .env
printf 'DOCKER_USERNAME=%s\nIMAGE_TAG=latest\n' <dockerhub-user> >> .env
docker login -u <dockerhub-user>
./deploy.sh
```

### Subsequent deployments

Every push to `main` builds, pushes and deploys automatically. Watch it in the
Actions tab; the job summary reports the deployed commit and the public URL.

### Verifying

```bash
curl http://<ec2-host>/health
# {"status":"healthy","provisioner":"Local","utc":"…"}

cd /opt/promoengine
docker compose ps                              # all three Up, two healthy
docker compose logs -f backend

# list the tenant databases — proof the per-tenant architecture is live
docker compose exec postgres psql -U postgres -c "\l" | grep PromoEngine
```

---

## 7. Image naming and tagging

```
docker.io/<DOCKER_USERNAME>/promoengine-backend:latest
docker.io/<DOCKER_USERNAME>/promoengine-backend:<commit-sha>
docker.io/<DOCKER_USERNAME>/promoengine-frontend:latest
docker.io/<DOCKER_USERNAME>/promoengine-frontend:<commit-sha>
```

Both tags are pushed on every build. `latest` is a convenience for a manual
pull; **the deployment always pins the commit SHA** — `IMAGE_TAG` in `.env` is
set to `github.sha`. Every running container is therefore traceable to an exact
commit, two deploys can never race on a moving tag, and a rollback is:

```bash
cd /opt/promoengine
sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=<older-sha>/' .env
docker compose up -d       # tenant data untouched
```

Layer caching keeps builds short: each Dockerfile copies its dependency
manifests (`*.csproj`, `package-lock.json`) and restores *before* copying
source, so a code-only change reuses the restore layer. The pipeline also
carries a GitHub Actions layer cache (`cache-from`/`cache-to: type=gha`) scoped
per image.

---

## 8. Production recommendations

**Do these before taking real traffic:**

1. **Terminate TLS.** Everything here is HTTP; a JWT over plain HTTP is
   readable by anything on the path. Either put an ALB with an ACM certificate
   in front (set `HTTP_PORT=8080` and point the target group at it), or add
   Caddy/certbot on the host. Then set `PUBLIC_ORIGIN=https://…` so the CORS
   allow-list matches.
2. **Replace the placeholder secrets.** `appsettings.json` contains a
   placeholder JWT key and a sample external API key. They are overridden by the
   environment in this setup — confirm with
   `docker compose exec backend printenv Jwt__SigningKey`.
3. **Use a Docker Hub access token**, not your account password, for
   `DOCKER_PASSWORD`, scoped to read/write on these two repositories.
4. **Restrict SSH.** Port 22 open to `0.0.0.0/0` is the most likely way this
   host is compromised. Restrict to known IPs, or use AWS Systems Manager
   Session Manager and drop the inbound rule entirely.
5. **Back up the volume.** The volume survives redeployment but not instance
   termination, and it now holds *every tenant's* data. Nightly:
   ```bash
   docker compose exec -T postgres pg_dumpall -U postgres | gzip \
     > /backup/promoengine-$(date +%F).sql.gz
   ```
   Ship it to S3 with lifecycle expiry, and rehearse a restore.

**Worth doing next:**

6. **Pin the SSH host key.** The pipeline uses `ssh-keyscan`, which trusts
   whatever answers on the first connection. Store the real key as a secret and
   write it to `known_hosts` instead.
7. **Run Nginx unprivileged.** Swap the base image for
   `nginxinc/nginx-unprivileged:1.27-alpine`, change the server block to
   `listen 8080`, and map `80:8080`. (The backend already runs as non-root.)
8. **Move off Docker Hub's rate limits** to Amazon ECR, and give the EC2
   instance an IAM role so no registry password is stored on the host at all.
9. **Cap container resources.** One runaway tenant query should not be able to
   starve the other containers — add `deploy.resources.limits` per service.
10. **Reconsider `Tenancy__AutoSyncSchemaOnStartup` as tenant count grows.** It
    sweeps every tenant database on boot, which is right for tens of tenants and
    wrong for thousands. The code already anticipates this — set it to `false`
    and run reconciliation as a separate job.
11. **Move tenant connection strings out of the catalog.** Storing credentials
    in a database column is what makes password rotation a maintenance window.
    AWS Secrets Manager references would fix it — a code change, noted here as
    the natural follow-up.

**Already handled:** non-root backend container; no published port for the
backend or PostgreSQL; secrets outside source control and outside image layers;
health checks on all three services; `restart: unless-stopped` everywhere; log
rotation; same-origin API so CORS is not an attack surface; and a deploy path
that cannot delete a volume.
