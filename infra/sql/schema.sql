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
