# CmCSP – Deployment with Azure Developer CLI (`azd`)

This guide covers deploying CmCSP with the **Azure Developer CLI**. It provisions
all infrastructure from the Bicep modules in `bicep/` and handles secrets, exports,
and image builds via hooks.

---

## How it maps

| Concern | Handled by |
|---|---|
| Resource group + app infra + storage + RBAC | `infra/main.bicep` (subscription scope) composing `infra/modules/app.bicep` + `infra/modules/storage.bicep` |
| Web app image build / push / revision roll | `azd deploy` (service `web`, .NET SDK container build — no Dockerfile) |
| Key Vault secrets, Container App env-var wiring | `infra/hooks/postprovision.ps1` |
| Cost Management exports (subscription **or** billing scope) | `infra/hooks/postprovision.ps1` (scope switch) |
| Cost collector Job image build / push / update | `infra/hooks/postdeploy.ps1` |

The old multi-pass managed-identity flow is collapsed into module ordering:
`app` deploys first, then `storage` consumes its MI principal IDs for declarative
RBAC — no second deployment pass and no stale role-assignment cleanup.

---

## Pre-requisites

| Requirement | Notes |
|---|---|
| Azure Developer CLI | `azd version` — install from <https://aka.ms/azd> |
| Azure CLI ≥ 2.60 | `az --version` — used by the hooks |
| .NET 10 SDK | `dotnet --version` — container build/push, no Docker required |
| PowerShell 7+ | `pwsh --version` — the hooks are PowerShell |
| App registration | Tenant ID, Client ID, Client Secret with Cost Management access |

---

## First-time deployment

```pwsh
# 1. Authenticate
azd auth login
az login                      # hooks use az for KV / exports

# 2. Create an environment (drives resource naming + the azd-env-name tag)
azd env new cmcsp-prod

# 3. Identity + subscriptions consumed by the app
azd env set CMCSP_TENANT_ID        <tenant-guid>
azd env set CMCSP_CLIENT_ID        <app-client-id>
azd env set CMCSP_CLIENT_SECRET    <app-client-secret>
azd env set CMCSP_SUBSCRIPTION_IDS <guid1,guid2,...>

# 4. Choose the Cost Management export scope (see below)
azd env set EXPORT_SCOPE subscription

# 5. Provision infra + deploy the app in one step
azd up
```

`azd up` runs **provision** (Bicep) → **postprovision** (secrets, env vars,
exports) → **deploy** (web image) → **postdeploy** (collector job image).

---

## Export scope switch (subscription ↔ billing account)

The export scope is selected with the `EXPORT_SCOPE` environment variable and
applied by the postprovision hook.

| `EXPORT_SCOPE` | Template | Auth | Deployment | Extra config |
|---|---|---|---|---|
| `subscription` | `infra/modules/export-sub.bicep` | Managed identity | `az deployment sub create` | — |
| `billing` | `infra/modules/export-billing.bicep` | SAS token | `az deployment tenant create` (tenant scope) | `BILLING_ACCOUNT_ID` |
| `none` (default) | — | — | skipped | — |

Switch scope at any time, then re-run provisioning:

```pwsh
# Subscription scope
azd env set EXPORT_SCOPE subscription
azd provision

# Billing account scope (requires tenant-level deploy permissions)
azd env set EXPORT_SCOPE billing
azd env set BILLING_ACCOUNT_ID <billing-account-id>
azd provision
```

> Billing-account exports cannot use managed identity. The hook generates a
> short-lived container SAS from the storage account key and passes it to
> `export-billing.bicep`. Tenant-scope deployment requires Global Admin or
> Billing Account Owner.

---

## Optional settings

```pwsh
# Cost Details API (reservations + amortized cost)
azd env set ENABLE_COST_DETAILS true

# Custom export name / historical backfill (subscription scope only)
azd env set EXPORT_NAME       cmcsp-daily-export
azd env set HISTORICAL_MONTHS 3

# Adopt existing resources instead of deriving new globally-unique names
azd env set AZURE_RESOURCE_GROUP   rg-cmcsp-app
azd env set ACR_NAME               cmcspacrXXXXXX
azd env set KEY_VAULT_NAME         kv-cmcsp-XXXXXX
azd env set STORAGE_ACCOUNT_NAME   cmcspstXXXXXX
```

---

## Day-2 operations

```pwsh
azd deploy        # rebuild + roll the web app image only
azd provision     # re-apply infra / secrets / exports (e.g. after scope change)
azd up            # both
azd down          # tear down the environment's resources
azd env get-values  # inspect resolved outputs
```

---

## Security notes

- **`CMCSP_CLIENT_SECRET` is stored in `.azure/<env>/.env` in plaintext.** azd
  env values are not encrypted. The hook copies it into Key Vault, but if local
  plaintext is unacceptable, export it as a transient shell variable before
  `azd provision` instead of using `azd env set`.
- The `.azure/` directory is git-ignored.
- All runtime access uses managed identities; only the app-registration client
  secret is stored in Key Vault.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Required azd environment variable '...' is not set` | Run the corresponding `azd env set` and retry. |
| `azd deploy` cannot find the Container App | Confirm the `azd-service-name: web` tag exists — it is applied by `infra/modules/app.bicep` via the `azdServiceName` param. |
| Billing export fails with permission error | Tenant-scope deploy needs Global Admin / Billing Account Owner. |
| Need the underlying `az` steps | See [csp-deployment-guide.md](csp-deployment-guide.md) for the manual command-by-command flow. |
