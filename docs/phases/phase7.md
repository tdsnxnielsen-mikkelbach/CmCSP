# Phase 7 – Azure inventory & optimization (new ARM RBAC: Reader)

> Part of the [CmCSP roadmap](../todo.md). **Status: 📋 Planned.**

**Goal:** Move from "what did it cost" to "what is running and where can we save," by joining
cost data to **live Azure resource inventory**. Same managed-identity auth, but the app/collect
identity needs **Reader** (and `Microsoft.Consumption` recommendation read) on the target
subscriptions. Azure-only scope.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Azure Resource Graph inventory enrichment** | P1 | 📋 Planned | One KQL query to `POST .../providers/Microsoft.ResourceGraph/resources` returns every resource with tags/region/SKU/owner. Join to `CostFact` to enrich tag chargeback with real resource counts and map cost → actual resource (which CSVs alone can't). New `ResourceGraphService`; cache like other 60-min datasets |
| **Orphaned / untagged resource finder** | P2 | 📋 Planned | Resource Graph queries for unattached managed disks, idle public IPs, empty App Service plans, stopped-but-allocated VMs, and untagged resources → concrete "delete this, save €X" savings list. New dashboard page (e.g. "Optimization") |
| **Reservation / Savings Plan purchase recommendations + expiry** | P2 | 📋 Planned | `Microsoft.Consumption/reservationRecommendations` for "buy this RI/Savings Plan, save €X/yr"; `Microsoft.Capacity/reservationOrders` for scope + **expiry dates** so customers aren't surprised by a lapse. Extends the Reservations page |
