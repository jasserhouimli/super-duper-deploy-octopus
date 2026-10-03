# Octopus — .NET Mini Cloud (v0.1)

A minimal PaaS for deploying GitHub repositories: register an app, trigger a deployment,
the worker clones, `docker build`s, `docker run`s it, and the API reverse-proxies
traffic to `/apps/{slug}/`.

Built with .NET 10 as a modular monolith with vertical slices.

## Architecture

```text
Developer -> POST /api/apps { repoUrl } -> POST /api/apps/{id}/deployments
                                                    |
                                              Octopus.Worker
                                        Clone -> Build -> Start (docker)
                                                    |
Octopus.Api (EF Core + SQLite) <- status/logs <-+
     |
   YARP reverse proxy: /apps/{slug}/{**catch-all} -> 127.0.0.1:{port}
```

Modules (`src/Modules`): each owns its domain, no cycles.

| Module | Owns | Does NOT own |
|---|---|---|
| `Apps` | App entity, creation validation | builds, containers |
| `Deployments` | Deployment state, logs, `OctopusDbContext` (v0.1) | docker, git |
| `GitHub` | URL validation, shallow clone | deployment state |
| `Runtime` | docker build/run/stop, port allocation | git, routing |
| `Routing` | YARP dynamic config provider | deployment decisions |
| `BuildingBlocks` | Result, Slug, ProcessRunner | domain rules |

`Octopus.Api` = HTTP composition + transport. `Octopus.Worker` = background execution.
Domain behavior lives in modules, not in Api/Worker.

## Requirements

- .NET SDK 10 (`dotnet --info`)
- Docker Desktop / Engine (`docker --version`)
- git (`git --version`)

## Quickstart

```powershell
dotnet build Octopus.slnx
dotnet test Octopus.slnx

# Terminal 1 — API (http://localhost:5000)
dotnet run --project src/Octopus.Api

# Terminal 2 — worker (must share the same octopus.db / connection string)
dotnet run --project src/Octopus.Worker
```

Register + deploy (repo **must contain a `Dockerfile` at root** in v0.1;
container must listen on `8080` by default, e.g. `ASPNETCORE_URLS=http://+:8080`):

```powershell
$app = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/apps `
  -ContentType application/json `
  -Body '{"name":"demo","repoUrl":"https://github.com/owner/repo","branch":"main"}'
$app.id

Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/apps/$($app.id)/deployments" `
  -ContentType application/json -Body '{}'

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

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/health` | liveness |
| POST | `/api/apps` | `{name, repoUrl, branch?, containerPort?}` |
| GET | `/api/apps` | list |
| GET | `/api/apps/{id}` | one |
| DELETE | `/api/apps/{id}` | stops container, deletes history |
| POST | `/api/apps/{id}/deployments` | enqueue (`202`), `409` if one in progress |
| GET | `/api/apps/{id}/deployments` | last 50 |
| GET | `/api/deployments/{id}` | one |
| GET | `/api/deployments/{id}/logs?take=200` | bounded log tail |
| POST | `/api/apps/{id}/stop` | docker stop + mark Stopped |

Deployed apps: `GET /apps/{slug}/{path...}` (YARP, prefix stripped).

## Security (v0.1 enforced)

- `repoUrl`: `https://github.com/owner/repo` only; credentials rejected; length-bounded.
- No secret logging: logs use `owner/repo`, never full URLs/tokens.
- Containers: bound to `127.0.0.1` only, `--memory 512m --cpus 1.0`, fixed host-port range `5100-5999`.
- Process timeouts everywhere (clone 2m, build 10m, run 2m); log output truncated.
- Payload limits: names/branches/URLs length-checked; logs capped per-line and per-query.

## Known limits (honest)

- Single worker claim (no lease); run one worker replica for now.
- SQLite + `EnsureCreated` (no migrations yet); Postgres + EF migrations are next.
- Dockerfile required; `dotnet publish` buildpack is roadmap.
- No auth, no private repos, no custom domains, no env-var secrets store yet.
- Stale `Cloning/Building/Starting` deployments are requeued on worker restart.

## Roadmap

1. Postgres + EF Core migrations, per-app env vars (secret references, not values in logs).
2. GitHub webhooks (HMAC, idempotency key) -> auto-deploy.
3. `dotnet` buildpack (no Dockerfile needed) + health-gated traffic switch.
4. Auth (API keys/OIDC), per-app resource quotas, log streaming.
5. Multi-worker leases/heartbeats, blue/green, custom domains.

## Repo layout

```text
src/Octopus.Api        HTTP + YARP + EF Sqlite wiring
src/Octopus.Worker     DeploymentWorker (claim -> clone -> build -> start)
src/Modules/...        Apps, Deployments, GitHub, Runtime, Routing
src/BuildingBlocks     Result, Slug, ProcessRunner
tests/Octopus.Tests    validators, URL + slug rules (17 tests)
```
