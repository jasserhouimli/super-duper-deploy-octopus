# Octopus — .NET Mini Cloud (v0.2)

![ci](https://github.com/jasserhouimli/super-duper-deploy-octopus/actions/workflows/ci.yml/badge.svg)

A minimal PaaS for deploying GitHub repositories: register an app, trigger a deployment
(manually or via GitHub webhook), the worker clones, builds (`Dockerfile` or generated
dotnet buildpack), `docker run`s it, and the API reverse-proxies traffic to `/apps/{slug}/`.

## Architecture

```text
Developer -> POST /api/apps { repoUrl } -> POST /api/apps/{id}/deployments
GitHub push -> POST /api/hooks/github/{id} (HMAC) -> Queued deployment
                                                    |
                                              Octopus.Worker
                                   Clone -> BuildPlan -> Build -> Start (docker)
                                                     |
Octopus.Api (EF Core + SQLite) <- status/logs <-+
     |
   YARP reverse proxy: /apps/{slug}/{**catch-all} -> 127.0.0.1:{port}
```

Modules (`src/Modules`): each owns its domain, no cycles.

| Module | Owns | Does NOT own |
|---|---|---|
| `Apps` | App entity, creation validation, env-var keys/validation | builds, containers, secret values in logs |
| `Deployments` | Deployment state, logs, webhook intake/idempotency, env-var storage, `OctopusDbContext` | docker, git |
| `GitHub` | URL validation, shallow clone | deployment state |
| `Runtime` | build plans, docker build/run/stop (+ `--env-file` injection), readiness probes, port allocation | git, routing, secret values in logs |
| `Routing` | YARP dynamic config provider | deployment decisions |
| `BuildingBlocks` | Result, Slug, ProcessRunner | domain rules |

`Octopus.Api` = HTTP composition + transport. `Octopus.Worker` = background execution.
Domain behavior lives in modules, not in Api/Worker.

## Requirements

- .NET SDK 10 (`dotnet --info`; pinned via `global.json`)
- Docker Desktop / Engine running (`docker info`)
- git (`git --version`)

## Run with Docker Compose (Linux host)

```powershell
docker compose up --build -d
curl http://localhost:5000/health
docker compose logs -f
docker compose down   # data persists in the octopus-data volume
```

Api + worker share one SQLite file and workspace through the `octopus-data`
volume; the worker builds/runs app containers through the host's Docker socket.
Linux-first: `network_mode: host` does not work on Docker Desktop for
Windows/Mac — there, run Api + Worker via `dotnet run` (see below).

## Run with dotnet (Windows/Mac/Linux)

```powershell
dotnet build Octopus.slnx
dotnet test Octopus.slnx

# Terminal 1 — API (http://localhost:5000)
dotnet run --project src/Octopus.Api

# First call ever: bootstrap an API key (shown once — save it):
# $key = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/keys `
#   -ContentType application/json -Body '{"name":"admin"}'
# $h = @{ Authorization = "Bearer $($key.key)" }  # use on all /api calls below

# Terminal 2 — worker (must share the same connection string / DB file)
$env:ConnectionStrings__Octopus = "Data Source=C:\data\octopus.db"
dotnet run --project src/Octopus.Api    # same env in terminal 1
dotnet run --project src/Octopus.Worker # same env in terminal 2
```

> The default `Data Source=octopus.db` is relative to each project's working
> directory, so Api and Worker get *different* files unless you set an absolute
> `ConnectionStrings__Octopus` (and matching `Octopus__Workspace`) in both.
> Existing DB files are upgraded in place on startup (`DbBootstrap`); Postgres +
> real EF migrations are on the roadmap.

## Postgres (opt-in)

`docker-compose.yml` ships a `db` service (`postgres:17-alpine`, data in the
`pgdata` volume) that is idle until you point both services at it — SQLite
stays the default. Any connection string containing `Host=` selects the Npgsql
provider automatically (`OctopusDbOptions`):

```powershell
# .env next to docker-compose.yml (never commit real passwords):
"POSTGRES_PASSWORD=long-random-value" | Out-File -Encoding ascii .env
```

Then uncomment the `ConnectionStrings__Octopus` Postgres lines in both the
`api` and `worker` services and `docker compose up --build -d`. On Postgres
the schema is applied with `MigrateAsync` from the checked-in `InitialCreate`
migration (`src/Modules/Deployments/Octopus.Deployments/Migrations`); new
schema changes land as further `dotnet ef` migrations. SQLite keeps
`EnsureCreated` + `DbBootstrap` for zero-setup dev.

Register + deploy (container must listen on `8080` by default,
e.g. `ASPNETCORE_URLS=http://+:8080`):

```powershell
$app = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/apps `
  -ContentType application/json `
  -Body '{"name":"demo","repoUrl":"https://github.com/owner/repo","branch":"main"}'
$app.id

# Per-app env vars (values stored, never listed/logged; injected at docker run):
Invoke-RestMethod -Method Put -Uri "http://localhost:5000/api/apps/$($app.id)/env" `
  -ContentType application/json -Body '{"vars":{"API_KEY":"s3cret","PORT":"8080"}}'
Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/env"  # -> keys only

# Dockerfile repo, or dotnet web project (buildpack), optionally pinned:
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/apps/$($app.id)/deployments" `
  -ContentType application/json -Body '{"projectPath":"src/Web/Web.csproj"}'
# ...or a nested Dockerfile for monorepos (not both):
# -Body '{"dockerfilePath":"deploy/prod/Dockerfile"}'

Invoke-RestMethod http://localhost:5000/api/apps
# open: http://localhost:5000/apps/demo/
```

Logs:

```powershell
$dep = (Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/deployments" -Headers $h)[0].id
Invoke-RestMethod "http://localhost:5000/api/deployments/$dep/logs?take=200" -Headers $h
# live tail (SSE, needs an authenticated client; curl example):
# curl -N -H "Authorization: Bearer $OCTOPUS_KEY" "http://localhost:5000/api/deployments/$dep/logs/stream"
```

Stop:

```powershell
Invoke-RestMethod -Method Post "http://localhost:5000/api/apps/$($app.id)/stop" -Headers $h
```

Stop halts the container and cancels queued deployments for the app. An
attempt the worker already claimed runs to completion (v1 has no worker
interruption); cancel a queued deployment any time with
`POST /api/deployments/{id}/cancel`, and requeue a failed one with
`POST /api/deployments/{id}/retry`.

## GitHub webhook auto-deploy

One-time setup per app (do this over localhost or a trusted network — the secret
is shown once):

```powershell
$tok = Invoke-RestMethod -Method Post "http://localhost:5000/api/apps/$($app.id)/webhook-token"
$tok.secret      # -> paste into GitHub: Settings > Webhooks > Secret
$tok.webhookUrl  # -> Payload URL, content type application/json, push events
```

Flow per delivery: `authenticate (HMAC-SHA256)` -> `persist receipt` ->
`idempotency (AppId, DeliveryId)` -> `branch filter` -> `queue deployment` -> `202`.
Redeliveries return the original deployment (`duplicate: true`); pushes to other
branches are ignored; a push while a deploy is active gets `409` (redeliver to retry).

```powershell
Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/webhook-events?take=50"
```

## Dotnet buildpack

No `Dockerfile`? The worker detects the shallowest `Microsoft.NET.Sdk.Web` project
(`bin`/`obj`/`.git`/`node_modules` excluded, depth ≤ 4) and generates a multi-stage
`.NET 10` Dockerfile into its throwaway workspace copy (never committed to your repo).
A repo `Dockerfile` always wins. Override per deployment via `projectPath`
(relative `.csproj`, no `..`). Monorepos can point at a nested Dockerfile via
`dockerfilePath` (relative, no `..`, must exist — built with `docker build -f`):

## Health-gated blue/green deploys

The new image starts as a sidecar beside the live container on a fresh port
(`5100-5999`). The worker waits up to 30 s for TCP readiness and checks
`docker inspect` state (images with a `HEALTHCHECK` must report `healthy`).
Only then is the sidecar promoted (`docker rename` to the canonical name, old
container stopped) and the route switched. A bad image is removed and the
deployment marked `Failed` — the previous container keeps serving throughout.
Probes are loopback-only, so there is no SSRF surface.

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/health` | liveness |
| POST | `/api/apps` | `{name, repoUrl, branch?, containerPort?, memoryMb?, cpuMillicores?}` |
| PATCH | `/api/apps/{id}` | `{branch}` — retarget branch (name/slug immutable) |
| PUT | `/api/apps/{id}/quota` | `{memoryMb?, cpuMillicores?}` — allowlisted caps |
| GET | `/api/apps` | list |
| GET | `/api/apps/{id}` | one |
| DELETE | `/api/apps/{id}` | stops container, deletes history + webhooks |
| POST | `/api/apps/{id}/deployments` | `{containerPort?, projectPath?, dockerfilePath?}`; `202`, `409` if one in progress |
| GET | `/api/apps/{id}/deployments` | last 50 (`?status=Failed` filters, case-insensitive) |
| GET | `/api/deployments/{id}` | one |
| POST | `/api/deployments/{id}/cancel` | cancel a queued deployment (`409` once claimed) |
| POST | `/api/deployments/{id}/retry` | requeue a failed/cancelled deployment (`202`, `409` if one in progress) |
| GET | `/api/deployments/{id}/logs?take=200` | bounded log tail |
| GET | `/api/deployments/{id}/logs/stream?afterId=0` | SSE stream: replay then live lines, `event: done` at terminal state (5 min cap) |
| POST | `/api/apps/{id}/stop` | docker stop + mark Stopped |
| POST | `/api/apps/{id}/webhook-token` | create/rotate secret (shown once) |
| POST | `/api/keys` | create API key `{name}`; raw key shown once |
| GET | `/api/keys` | list keys (prefixes only, never hashes) |
| POST | `/api/keys/{id}/revoke` | revoke a key |
| GET | `/api/apps/{id}/webhook-events?take=50` | delivery receipts |
| GET | `/api/apps/{id}/env` | list env key names only (values never returned) |
| PUT | `/api/apps/{id}/env` | replace env set `{vars:{KEY:value}}`; max 50 vars, 8 KB/value, 64 KB total |
| DELETE | `/api/apps/{id}/env/{key}` | remove one variable |
| POST | `/api/hooks/github/{id}` | GitHub receiver (`X-GitHub-Event/Delivery`, `X-Hub-Signature-256`) |

Deployed apps: `GET /apps/{slug}/{path...}` (YARP, prefix stripped).

Deployment history is capped at the newest 50 per app (with their logs) —
the worker prunes older records when a deployment reaches a terminal state.

## Security (enforced)

- Control API auth: `Authorization: Bearer oct_...` on all `/api` routes except
  `/health` and the HMAC-authenticated GitHub receiver. Keys are `oct_` +
  base64url(32 bytes); only SHA-256 hashes are stored (constant-time verify).
  Bootstrap: with zero active keys, `POST /api/keys {name}` is allowed once
  to create the first key; afterwards it requires auth like everything else. List/revoke expose prefixes only.
- `repoUrl`: `https://github.com/owner/repo` only; credentials rejected; length-bounded.
- Webhooks: HMAC-SHA256 with constant-time compare; 1 MB payload cap; unauthenticated
  payloads are never persisted; secrets live in the control-plane DB, returned only
  at creation/rotation, never logged.
- No secret logging anywhere: logs use `owner/repo`, never full URLs/tokens.
- App env vars: values stored in the control-plane DB, injected via a throwaway
  docker `--env-file` (never in `docker run -e` args), list endpoints return key
  names only, logs record key names/count only. Keys `^[A-Za-z_][A-Za-z0-9_]*$`
  (max 64), values max 8 KB without NUL/newlines, max 50 vars / 64 KB per app.
- Containers: bound to `127.0.0.1` only, per-app quotas (memory `128/256/512/1024/2048` MB,
  CPU `250/500/1000/2000` millicores; defaults `512`/`1000`), fixed host-port range `5100-5999`.
- Process timeouts everywhere (clone 2m, build 10m, run 2m); log output truncated.
- Payload limits: names/branches/URLs/paths length-checked; logs capped per-line and per-query.

## Known limits (honest)

- Claims are lease-based (2 min, 30 s heartbeat, optimistic-concurrency races
  resolve to one winner); a restarting worker only requeues deployments whose
  lease lapsed. Concurrent builds still share one Docker host, so run one worker
  per host for now.
- SQLite + `DbBootstrap` (no real migrations yet); Postgres + EF migrations are next.
- No auth on the control API, no private repos, no custom domains, no env-var secrets store yet.
- Stale `Cloning/Building/Starting` deployments are requeued on worker restart.
- SQLite cannot `ORDER BY DateTimeOffset` server-side: list queries prefetch a bounded
  window and sort in memory (see `DeploymentQueries`, covered by `SqliteOrderingTests`).

## Roadmap

1. Postgres + EF Core migrations; ~~per-app env vars (secret references, not values in logs)~~ Done (v0.4).
2. ~~GitHub webhooks (HMAC, idempotency key) -> auto-deploy.~~ Done (v0.2).
3. ~~`dotnet` buildpack (no Dockerfile needed)~~ Done (v0.2); ~~health-gated traffic switch~~ Done (v0.4: TCP readiness + inspect gate, bad images fail instead of routing).
4. ~~CI (build + test + architecture tests + image builds) and compose.~~ Done (v0.3).
5. ~~API-key auth~~ Done (v0.5); ~~log streaming~~ Done (v0.5: cursor tail + SSE stream). OIDC is future.
6. ~~Multi-worker leases/heartbeats~~ Done (v0.6). ~~Blue/green~~ Done (v0.7: sidecar start, readiness gate, rename promote). Remaining: custom domains.

## Repo layout

```text
src/Octopus.Api        HTTP + YARP + EF Sqlite wiring
src/Octopus.Worker     DeploymentWorker (lease claim -> clone -> build-plan -> build -> start w/ env-file, heartbeat, prune)
src/Modules/...        Apps (+EnvVars), Deployments (+Webhooks, EnvVar storage), GitHub, Runtime (+Buildpack, env-file), Routing
src/BuildingBlocks     Result, Slug, ProcessRunner
tests/Octopus.Tests    validators, webhooks, buildpack, env vars, quotas, auth, leases, health probes, sqlite ordering, architecture (157 tests)
```
