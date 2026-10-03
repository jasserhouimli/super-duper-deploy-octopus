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
| `Apps` | App entity, creation validation | builds, containers |
| `Deployments` | Deployment state, logs, webhook intake/idempotency, `OctopusDbContext` | docker, git |
| `GitHub` | URL validation, shallow clone | deployment state |
| `Runtime` | build plans, docker build/run/stop, port allocation | git, routing |
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

Register + deploy (container must listen on `8080` by default,
e.g. `ASPNETCORE_URLS=http://+:8080`):

```powershell
$app = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/apps `
  -ContentType application/json `
  -Body '{"name":"demo","repoUrl":"https://github.com/owner/repo","branch":"main"}'
$app.id

# Dockerfile repo, or dotnet web project (buildpack), optionally pinned:
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/apps/$($app.id)/deployments" `
  -ContentType application/json -Body '{"projectPath":"src/Web/Web.csproj"}'

Invoke-RestMethod http://localhost:5000/api/apps
# open: http://localhost:5000/apps/demo/
```

Logs:

```powershell
$dep = (Invoke-RestMethod "http://localhost:5000/api/apps/$($app.id)/deployments")[0].id
Invoke-RestMethod "http://localhost:5000/api/deployments/$dep/logs?take=200"
```

Stop:

```powershell
Invoke-RestMethod -Method Post "http://localhost:5000/api/apps/$($app.id)/stop"
```

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
(relative `.csproj`, no `..`).

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/health` | liveness |
| POST | `/api/apps` | `{name, repoUrl, branch?, containerPort?}` |
| GET | `/api/apps` | list |
| GET | `/api/apps/{id}` | one |
| DELETE | `/api/apps/{id}` | stops container, deletes history + webhooks |
| POST | `/api/apps/{id}/deployments` | `{containerPort?, projectPath?}`; `202`, `409` if one in progress |
| GET | `/api/apps/{id}/deployments` | last 50 |
| GET | `/api/deployments/{id}` | one |
| GET | `/api/deployments/{id}/logs?take=200` | bounded log tail |
| POST | `/api/apps/{id}/stop` | docker stop + mark Stopped |
| POST | `/api/apps/{id}/webhook-token` | create/rotate secret (shown once) |
| GET | `/api/apps/{id}/webhook-events?take=50` | delivery receipts |
| POST | `/api/hooks/github/{id}` | GitHub receiver (`X-GitHub-Event/Delivery`, `X-Hub-Signature-256`) |

Deployed apps: `GET /apps/{slug}/{path...}` (YARP, prefix stripped).

## Security (enforced)

- `repoUrl`: `https://github.com/owner/repo` only; credentials rejected; length-bounded.
- Webhooks: HMAC-SHA256 with constant-time compare; 1 MB payload cap; unauthenticated
  payloads are never persisted; secrets live in the control-plane DB, returned only
  at creation/rotation, never logged.
- No secret logging anywhere: logs use `owner/repo`, never full URLs/tokens.
- Containers: bound to `127.0.0.1` only, `--memory 512m --cpus 1.0`, fixed host-port range `5100-5999`.
- Process timeouts everywhere (clone 2m, build 10m, run 2m); log output truncated.
- Payload limits: names/branches/URLs/paths length-checked; logs capped per-line and per-query.

## Known limits (honest)

- Single worker claim (no lease); run one worker replica for now.
- SQLite + `DbBootstrap` (no real migrations yet); Postgres + EF migrations are next.
- No auth on the control API, no private repos, no custom domains, no env-var secrets store yet.
- Stale `Cloning/Building/Starting` deployments are requeued on worker restart.
- SQLite cannot `ORDER BY DateTimeOffset` server-side: list queries prefetch a bounded
  window and sort in memory (see `DeploymentQueries`, covered by `SqliteOrderingTests`).

## Roadmap

1. Postgres + EF Core migrations, per-app env vars (secret references, not values in logs).
2. ~~GitHub webhooks (HMAC, idempotency key) -> auto-deploy.~~ Done (v0.2).
3. ~~`dotnet` buildpack (no Dockerfile needed)~~ Done (v0.2); next: health-gated traffic switch.
4. ~~CI (build + test + architecture tests + image builds) and compose.~~ Done (v0.3).
5. Auth (API keys/OIDC), per-app resource quotas, log streaming.
6. Multi-worker leases/heartbeats, blue/green, custom domains.

## Repo layout

```text
src/Octopus.Api        HTTP + YARP + EF Sqlite wiring
src/Octopus.Worker     DeploymentWorker (claim -> clone -> build-plan -> build -> start)
src/Modules/...        Apps, Deployments (+Webhooks), GitHub, Runtime (+Buildpack), Routing
src/BuildingBlocks     Result, Slug, ProcessRunner
tests/Octopus.Tests    validators, webhooks, buildpack, sqlite ordering, architecture (44 tests)
```
