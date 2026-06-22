-- =============================================================================
-- CmCSP Phase 4 — SQL data platform schema
-- =============================================================================
-- Target: Azure SQL Database (serverless tier), Entra-only auth (no SQL logins).
-- This file is the canonical reference for the schema created by EF Core migrations
-- (CmcspDbContext). It can also be applied directly (e.g. by the provisioning hook)
-- for environments that prefer script-based DDL over `dotnet ef database update`.
--
-- Idempotent: safe to run repeatedly.
-- =============================================================================

-- ── CostFact: durable store of parsed/aggregated cost rows ───────────────────
IF OBJECT_ID(N'dbo.CostFact', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CostFact
    (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CostFact PRIMARY KEY,
        Dataset           NVARCHAR(16)  NOT NULL,
        UsageDate         DATE          NOT NULL,
        SubscriptionId    NVARCHAR(36)  NOT NULL,
        SubscriptionName  NVARCHAR(256) NOT NULL CONSTRAINT DF_CostFact_SubName DEFAULT(N''),
        ServiceName       NVARCHAR(256) NOT NULL CONSTRAINT DF_CostFact_Service DEFAULT(N''),
        ResourceGroupName NVARCHAR(256) NOT NULL CONSTRAINT DF_CostFact_Rg      DEFAULT(N''),
        Tag               NVARCHAR(512) NOT NULL CONSTRAINT DF_CostFact_Tag     DEFAULT(N''),
        Cost              DECIMAL(38,18) NOT NULL,
        Currency          NVARCHAR(8)   NOT NULL,
        NormalizedCost    DECIMAL(38,18) NOT NULL
    );

    -- Natural key: one row per dataset/day/sub/grouping/currency.
    -- Backfill + re-collection upsert against this (latest write wins).
    CREATE UNIQUE INDEX UX_CostFact_NaturalKey
        ON dbo.CostFact (Dataset, UsageDate, SubscriptionId, ServiceName, ResourceGroupName, Tag, Currency);

    -- Dashboard query shape: a dataset over a date range.
    CREATE INDEX IX_CostFact_Dataset_UsageDate
        ON dbo.CostFact (Dataset, UsageDate);
END;
GO

-- ── CostFact: Phase 9 tenant dimension (additive, idempotent) ────────────────
-- Carried for per-customer query scoping; the natural key is unchanged.
IF COL_LENGTH(N'dbo.CostFact', N'CustomerId') IS NULL
BEGIN
    ALTER TABLE dbo.CostFact
        ADD CustomerId BIGINT        NOT NULL CONSTRAINT DF_CostFact_CustomerId DEFAULT(0),
            TenantId   NVARCHAR(36)  NOT NULL CONSTRAINT DF_CostFact_TenantId   DEFAULT(N'');
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostFact_Customer_Dataset_UsageDate' AND object_id = OBJECT_ID(N'dbo.CostFact'))
BEGIN
    CREATE INDEX IX_CostFact_Customer_Dataset_UsageDate
        ON dbo.CostFact (CustomerId, Dataset, UsageDate);
END;
GO

-- ── CollectionAudit: cost-collection run history ─────────────────────────────
IF OBJECT_ID(N'dbo.CollectionAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CollectionAudit
    (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionAudit PRIMARY KEY,
        Status            NVARCHAR(32)  NOT NULL,
        [Trigger]         NVARCHAR(32)  NOT NULL,
        StartedUtc        DATETIMEOFFSET NOT NULL,
        FinishedUtc       DATETIMEOFFSET NOT NULL,
        DurationMs        BIGINT        NOT NULL,
        SubscriptionCount INT           NOT NULL,
        MainRows          INT           NOT NULL,
        RgRows            INT           NOT NULL,
        TagRows           INT           NOT NULL,
        AmortRows         INT           NOT NULL,
        Error             NVARCHAR(4000) NULL,
        ReplicaName       NVARCHAR(128) NULL,
        CorrelationId     NVARCHAR(64)  NOT NULL
    );

    CREATE INDEX IX_CollectionAudit_StartedUtc
        ON dbo.CollectionAudit (StartedUtc DESC);
END;
GO

-- ── UserSubscription: runtime UI-added subscription IDs ──────────────────────
IF OBJECT_ID(N'dbo.UserSubscription', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserSubscription
    (
        SubscriptionId NVARCHAR(36) NOT NULL CONSTRAINT PK_UserSubscription PRIMARY KEY,
        AddedUtc       DATETIMEOFFSET NOT NULL
    );
END;
GO

-- ── AppSetting: small runtime key/value flags (e.g. CostDetails.Enabled) ─────
IF OBJECT_ID(N'dbo.AppSetting', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppSetting
    (
        [Key]      NVARCHAR(128)  NOT NULL CONSTRAINT PK_AppSetting PRIMARY KEY,
        [Value]    NVARCHAR(4000) NOT NULL,
        UpdatedUtc DATETIMEOFFSET NOT NULL
    );
END;
GO

-- ── Customer: Phase 9 CSP multi-tenancy — a reseller's customer (one tenant) ─
IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customer
    (
        Id                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
        TenantId           NVARCHAR(36)  NOT NULL,
        DisplayName        NVARCHAR(256) NOT NULL,
        Status             NVARCHAR(16)  NOT NULL CONSTRAINT DF_Customer_Status DEFAULT(N'active'),
        GdapRelationshipId NVARCHAR(128) NULL,
        CreatedUtc         DATETIMEOFFSET NOT NULL
    );

    -- One customer per tenant; also the reverse lookup key during authorization.
    CREATE UNIQUE INDEX UX_Customer_TenantId
        ON dbo.Customer (TenantId);
END;
GO

-- ── CustomerSubscription: which subscriptions belong to a customer ───────────
IF OBJECT_ID(N'dbo.CustomerSubscription', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerSubscription
    (
        Id               BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerSubscription PRIMARY KEY,
        CustomerId       BIGINT        NOT NULL,
        SubscriptionId   NVARCHAR(36)  NOT NULL,
        SubscriptionName NVARCHAR(256) NOT NULL CONSTRAINT DF_CustSub_Name DEFAULT(N''),
        AddedUtc         DATETIMEOFFSET NOT NULL,
        CONSTRAINT FK_CustomerSubscription_Customer
            FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (Id)
    );

    CREATE UNIQUE INDEX UX_CustomerSubscription_Customer_Sub
        ON dbo.CustomerSubscription (CustomerId, SubscriptionId);

    -- Reverse lookup subscription → customer used during authorization.
    CREATE INDEX IX_CustomerSubscription_Sub
        ON dbo.CustomerSubscription (SubscriptionId);
END;
GO

-- ── Bootstrap "home" customer ────────────────────────────────────────────────
-- The single-tenant deployment's existing CostFact rows belong to the CSP's own tenant.
-- Seed customer #1 so nothing is orphaned once tenant scoping is enforced. The home
-- TenantId is supplied at deploy time via the :home_tenant_id sqlcmd variable (the
-- configured AzureCostManagement:TenantId); the placeholder below keeps the script
-- runnable standalone. Idempotent: only inserts when the table is empty.
IF NOT EXISTS (SELECT 1 FROM dbo.Customer)
BEGIN
    INSERT INTO dbo.Customer (TenantId, DisplayName, Status, CreatedUtc)
    VALUES (N'$(home_tenant_id)', N'Home tenant', N'active', SYSDATETIMEOFFSET());
END;
GO

-- Backfill: stamp any unassigned CostFact rows onto the home customer so every fact
-- has an owning customer once per-customer scoping is enforced.
IF EXISTS (SELECT 1 FROM dbo.Customer)
   AND EXISTS (SELECT 1 FROM dbo.CostFact WHERE CustomerId = 0)
BEGIN
    DECLARE @homeCustomerId BIGINT =
        (SELECT TOP (1) Id FROM dbo.Customer ORDER BY Id ASC);
    DECLARE @homeTenantId NVARCHAR(36) =
        (SELECT TOP (1) TenantId FROM dbo.Customer ORDER BY Id ASC);

    UPDATE dbo.CostFact
        SET CustomerId = @homeCustomerId,
            TenantId   = @homeTenantId
        WHERE CustomerId = 0;
END;
GO
