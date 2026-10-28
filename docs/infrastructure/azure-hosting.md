# Azure Hosting

*Status: **Proposed, not decided** — 2026-10-28. A recommendation for hosting a dev and a prod deployment of
CreatorPantry; it records no decision the product owner has made and changes no code.*

## Recommendation

Host both deployments on **Azure Container Apps (ACA)**, each in **its own Container Apps environment**. Do not
use AKS.

## Why not AKS

The deployable surface is four long-running services — `gateway`, `web`, `api`, `worker` — and one run-once
`migrations` step. AKS adds cost and operational work that buys nothing at that size: node pools, cluster
upgrades, an ingress controller, cert-manager, and a system node pool paid for around the clock.

Aspire deploys to ACA directly (`aspire deploy` / azd), so the existing AppHost is most of the deployment
definition.

AKS becomes the right answer when CreatorPantry needs something ACA cannot provide: DaemonSets, custom
operators, a service mesh, GPU node pools, dozens of services, or a team that already runs Kubernetes. None
applies today.

## One environment or two

**Two — one per deployment.** An ACA environment is a network, logging and trust boundary: apps inside it reach
each other by internal DNS and share a Log Analytics workspace. A dev app able to call prod's API internally
contradicts the rest of the security model (gateway-signed ES256 internal tokens, workspace isolation — see
`.claude/rules/auth.md` and `.claude/rules/tenancy.md`).

On the Consumption workload profile an environment itself costs nothing; billing is per app. Isolation is
therefore essentially free. Use separate resource groups at minimum, and separate subscriptions where possible.

## Sizing

Use a **workload-profiles** environment on the **Consumption** profile. Add a Dedicated profile only when a
workload needs one; it adds a plan management fee plus node cost.

| App | Dev | Prod |
|---|---|---|
| `gateway` | 0.25 vCPU / 0.5 Gi, min 0–1 | 0.5 vCPU / 1 Gi, min 2, max 4 |
| `web` | 0.25 vCPU / 0.5 Gi, min 0 | 0.25 vCPU / 0.5 Gi, min 2 (or serve the bundle from a CDN later) |
| `api` | 0.5 vCPU / 1 Gi, min 1 | 1 vCPU / 2 Gi, min 2, max 6–10 on HTTP concurrency |
| `worker` | 0.5 vCPU / 1 Gi, min 1 | 1 vCPU / 2 Gi, min 1, max 3 |
| `migrations` | ACA **Job**, not an app | ACA **Job**, not an app |

- `worker` keeps a minimum of 1 because it polls SQL for work; nothing wakes it from zero unless a KEDA MSSQL
  scaler is added.
- `worker` gets 2 Gi in prod because image inspection and rendition work hold whole files in memory.
- Scale-to-zero in dev trades a cold start for cost, which is acceptable there and not in prod.

### Backing services

| Resource | Dev | Prod |
|---|---|---|
| Azure SQL Database | General Purpose serverless, auto-pause on | General Purpose, 2 vCore |
| Azure Managed Redis | Smallest tier | One tier up |
| Azure Storage | Standard LRS | Standard ZRS |

Azure SQL Database supports the `VECTOR` type, so semantic search is unaffected by leaving the SQL Server 2025
container behind.

## Changes required before the first deploy

Each is one atomic change.

### 1. Provision SQL Server and Redis as managed services, not containers

[`AppHost.cs`](../../src/CreatorPantry.AppHost/AppHost.cs) calls `AddSqlServer` and `AddRedis`. Published as-is,
those become containers in ACA backed by Azure Files — the wrong place for SQL Server data files, and a Redis
with no managed availability.

Switch to `AddAzureSqlServer(...).RunAsContainer()` and Azure Managed Redis with `.RunAsContainer()`. Local
`aspire run` keeps the containers it has today; a deployment provisions the managed services.

### 2. Persist Data Protection keys

[`EdgeSecurity.cs`](../../src/CreatorPantry.Gateway/Sessions/EdgeSecurity.cs) and
[`AuthServiceCollectionExtensions.cs`](../../src/CreatorPantry.Domain/Modules/Auth/AuthServiceCollectionExtensions.cs)
call `AddDataProtection()` with no `PersistKeysTo…`. Session tickets already live in Redis
(`DistributedTicketStore`), but every replica and every new revision generates its own key ring, so:

- a cookie issued by one gateway replica does not decrypt on the other;
- every deploy signs every creator out and invalidates antiforgery tokens.

Persist the key ring to blob storage and protect it with Key Vault (`PersistKeysToAzureBlobStorage` +
`ProtectKeysWithAzureKeyVault`). **This is the one that fails on day one** — prod runs two gateway replicas.

### 3. Run migrations as a job

A container app whose process exits is restarted. Publish `migrations` as an ACA Job — Aspire offers
`PublishAsAzureContainerAppJob`; confirm it against the Aspire version in use — or run it as a pipeline step
before the apps roll.

## Open questions

- Subscription layout: one subscription with two resource groups, or one subscription per environment.
- Whether `web` stays a container app in prod or the Angular bundle moves to a CDN in front of the gateway.
- Region, and whether prod needs zone redundancy on the ACA environment and Azure SQL from the start.
