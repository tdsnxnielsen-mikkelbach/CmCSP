# CSP setup and deployment

The dashboard runs on .NET 10 with Blazor Interactive Server, Radzen.Blazor, and
ApexCharts. Azure infrastructure and deployment are described in the
[azd guide](azd-deployment-guide.md).

## Setup sequence

1. Enable customer Azure cost visibility in Partner Center where required by the
   customer's agreement. `IndirectCostDisabled` is not fixed by Azure RBAC alone.
2. Create an Entra app registration and service principal. Register local and
   deployed `/signin-oidc` addresses as **Web** redirect URIs. Browser sign-in uses
   authorization-code flow; implicit grants are not required.
3. Grant the Azure cost and optional enrichment permissions in
   [Azure roles](azure-roles.md) to the identities actually obtaining API tokens.
   Keep only the Entra API permissions listed there; for each customer tenant follow
   the [onboarding checklist](azure-roles.md#customer-tenant-onboarding).
4. Authenticate the deployment operator with `az login` and `azd auth login`.
5. Configure the azd environment's tenant, app ID, subscription list, and export scope.
   Supply the app credential securely; the hook stores it in Key Vault.
6. Run `azd up`. For later code-only updates, run `azd deploy`.
7. Add the deployed HTTPS sign-in URI to the app registration and verify login.
8. Confirm export CSVs arrive, collection succeeds, and the dashboard has data.

## Runtime identities

The web app and collector have separate managed identities. Registry image pulls
use the configured pull identity. SQL, Redis, Key Vault, and Storage permissions
are separate from subscription Cost Management permissions.

The Entra application credential remains necessary for configured OIDC and
service-principal access; managed-identity data platform authentication does not
eliminate this credential.

## Exports and subscription onboarding

The Home page supports adding/importing/removing runtime subscriptions, detecting
export targets, and retrying provisioning. Configuration-defined subscriptions
are not removable there. Export creation can be rejected for application identities;
the supported deploy-time provisioning flow runs as the authorized deployer.
Use [onboard-subscription.ps1](../scripts/onboard-subscription.ps1) for the repository's
manual role-onboarding workflow when required.

Adding a subscription does not manufacture historical data. Verify its cost
visibility, permissions, export delivery, and collection run before investigating UI totals.

## Optional capabilities

- [SQL and Redis](phase4-data-platform.md): gated durable storage/shared cache.
- [Multi-tenancy](phase9-multitenancy.md): SQL customer registry, issuer validation,
  per-tenant access, and partner-only administration.
- [Reservations](cost-details-api.md): optional Cost Details and billing-customer scopes.
- [Collection operations](data-collection.md): schedule, manual fan-out, and backfill.

## Operational checks

Validate both the web revision and collector image after deployment. Check grid
sorting/paging, date and subscription filters, dark/light themes, ApexCharts,
and guided tours after a UI update. Keep secrets in approved secret stores;
override the public API credential securely rather than relying on source defaults.
