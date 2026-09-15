# Support Tickets Dashboard

An ASP.NET Core dashboard on Azure Container Apps that reads Databricks Lakebase
(Postgres) directly, using Microsoft Entra ID end to end. No database password
exists anywhere: the Lakebase project has native Postgres login disabled.

Live: `https://tickets-dashboard.politebush-8a7978e7.eastus2.azurecontainerapps.io`

## Architecture

```mermaid
flowchart LR
  U[User] -->|Entra sign-in| CA
  CA["Container App<br/>ASP.NET Core 10"] -->|1 managed identity| E[Entra ID]
  E -->|2 access token| W["Databricks workspace<br/>postgres/credentials API"]
  W -->|3 60-min credential| CA
  CA -->|4 pgwire + TLS VerifyFull| LB[("Lakebase Postgres<br/>lb_tickets_clean")]
```

The credential in step 3 travels in the Postgres password field. That is the
transport, not a stored secret — it is an Entra-derived token valid for 60
minutes, refreshed automatically by `NpgsqlDataSource` every 45.

## Layout

| Path | Purpose |
| --- | --- |
| `app/` | Dashboard: API, static UI, Dockerfile |
| `tests.dashboard/` | Unit tests for options and filter validation |
| `infra/main.bicep` | Container App, environment, registry, built-in auth |
| `setup/lakebase-role.sql` | Postgres role and read-only grants |

## Data

`dbdemos_aibi_customer_support.lb_tickets_clean` — 24,872 rows synced from Unity
Catalog, spanning 2024-05-01 to 2025-07-26 across EMEA, APAC and AMER.

## Permissions

Lakebase enforces two independent layers. Both are required; either alone fails.

| Layer | Grants | Where |
| --- | --- | --- |
| Workspace | Service principal with `workspace-access`, `CAN_USE` on the project | Databricks SCIM + project ACL |
| Database | Postgres role + **column-level** `SELECT` on seven columns | `setup/lakebase-role.sql` |

The grant is column-level on purpose. A table-wide `GRANT SELECT` also exposes
`call_transcript`, `compliance_data_leak` and lat/long, none of which the
dashboard reads. `COUNT(*)` still works, because Postgres permits it with SELECT
on any one column.

The Postgres role name is the managed identity's **client ID**, and it is
case-sensitive. The identity is referenced (not created) by the Bicep template,
because that client ID is baked into both layers — a new identity would silently
lose all access.

## Local development

Requires .NET 10 and an `az login` session that can reach the workspace.

```bash
dotnet run --project app --urls http://localhost:5180
```

`app/appsettings.Development.json` sets `Identity:UseDeveloperCredential`, which
switches to `AzureCliCredential`. That flag is rejected outside the Development
environment.

Build and test the whole solution:

```bash
dotnet build TicketsDashboard.slnx
dotnet test  TicketsDashboard.slnx
```

## First-time identity setup

Creating the identity is one step; wiring it up takes three more. Workspace-admin
rights in Databricks are sufficient — account-admin is not required.

1. Create it: `az identity create -g tickets-dashboard-rg -n tickets-id -l eastus2`
2. Register it in the workspace via SCIM (`/api/2.0/preview/scim/v2/ServicePrincipals`)
   with the `workspace-access` entitlement, using its **client ID** as `applicationId`
3. Grant `CAN_USE` on the project:
   `PATCH /api/2.0/permissions/database-projects/lakebaseproject1`
4. Run `setup/lakebase-role.sql`, substituting that same client ID

Skipping step 2 or 3 fails at the credential exchange; skipping step 4 fails at
the Postgres connection.

## Deploy

```bash
az acr build --registry ticketsacrjuimgm6jl3a66 --image tickets-dashboard:v2 ./app

$env:AUTH_CLIENT_SECRET = "<secret>"
az deployment group create -g tickets-dashboard-rg --parameters infra/demo.bicepparam
```

`az acr build` compiles in Azure, so Docker is not needed locally. The parameter
file pins the image **by digest**, not by tag: tags are mutable, so a rebuild
would otherwise silently change what runs. Update the digest on each release.

The client secret is read from the environment via `readEnvironmentVariable`, so
it is never committed. `infra/prod.bicepparam` shows the production shape.

After the first deployment, set the auth app registration's redirect URI to the
template's `redirectUri` output, or sign-in fails.

### Deployed resources

| Resource | Value |
| --- | --- |
| Resource group | `tickets-dashboard-rg` (eastus2) |
| Container App | `tickets-dashboard` |
| Registry | `ticketsacrjuimgm6jl3a66.azurecr.io` |
| Managed identity | `tickets-id` — `2135cf05-7867-43e6-b84f-d6e6fd7c90de` |
| Auth app registration | `tickets-dashboard-auth` — `23b870db-bb0b-433e-822c-71fe161819a6` |

The Container App and Lakebase both sit in eastus2; co-locating them matters,
since every dashboard request makes a round trip to Postgres.

## Two non-obvious constraints

**Use the direct endpoint host, not `-pooler`.** The PgBouncer pooler rejects
Lakebase OAuth credentials with `08P01: SASL authentication failed`. Connections
are pooled in-process instead. `LakebaseOptions` fails fast if a pooler host is
configured.

**The connection is warmed at startup.** Fetching the credential lazily made the
first request fail with `Authentication timed out`, because the token exchange
ran mid-handshake and Postgres cut it off. `ConnectionWarmup` opens one
connection before traffic arrives.

## Operational behaviour

| Concern | Handling |
| --- | --- |
| Telemetry | OpenTelemetry to Application Insights, including Npgsql spans. Disabled when no connection string is set, so local runs stay quiet. |
| Errors | `UseExceptionHandler` with ProblemDetails; no exception detail leaves the process in Production. |
| Rate limiting | Fixed window, 120 requests/minute, partitioned per signed-in user. |
| Credential refresh | Every 45 minutes against a 60-minute lifetime. |
| Transient failures | Standard resilience handler on the credential call; up to 3 connection attempts, since Lakebase resume can fail the first one. |

## Known gaps

Not production-ready without these, and they are deliberately not faked:

- **No private networking.** Ingress is public and traffic to Lakebase leaves
  over the internet, TLS-protected but not private. Requires VNet integration,
  private endpoints, and Container Registry Premium.
- **Auth secret is not in Key Vault.** Attempted; Key Vault in this subscription
  is forced to `publicNetworkAccess: Disabled`, so neither an operator nor the
  non-VNet Container App can reach it. Blocked on the networking item above.
- **Single zone.** `zoneRedundant` needs a VNet-integrated environment, which
  cannot be converted in place.

## Costs

`minReplicas: 1` keeps one replica warm so the rotating credential stays cached,
which means continuous billing rather than scale-to-zero.

The Lakebase compute suspends after 5 minutes idle (`suspend_timeout_duration:
300s`) and resumes on the next query. Databricks documents resume as a few
hundred milliseconds; that was not measured here.

## Measured performance

| | |
| --- | --- |
| Full-range dashboard query, in-region | ~210 ms |
| Warm filtered count | ~68 ms |
| Same full-range query from a laptop | ~500 ms |

The gap between the first and third rows is network distance, not database work.
