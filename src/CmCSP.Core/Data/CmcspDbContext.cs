using Microsoft.EntityFrameworkCore;

namespace CmCSP.Data;

/// <summary>
/// EF Core context for the CmCSP SQL data platform (Phase 4). Backed by Azure SQL
/// Database (serverless tier) and authenticated with the Container App / job managed
/// identity — the connection string uses <c>Authentication=Active Directory Default</c>,
/// so <c>Microsoft.Data.SqlClient</c> acquires a token via <c>DefaultAzureCredential</c>
/// with no secrets in config or Key Vault.
/// </summary>
public sealed class CmcspDbContext(DbContextOptions<CmcspDbContext> options) : DbContext(options)
{
    public DbSet<CostFact> CostFacts => Set<CostFact>();
    public DbSet<CollectionAuditEntity> CollectionAudit => Set<CollectionAuditEntity>();
    public DbSet<UserSubscriptionEntity> UserSubscriptions => Set<UserSubscriptionEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CostFact>(e =>
        {
            e.ToTable("CostFact");
            e.HasKey(x => x.Id);
            e.Property(x => x.Dataset).HasMaxLength(16).IsRequired();
            e.Property(x => x.UsageDate).HasColumnType("date");
            e.Property(x => x.SubscriptionId).HasMaxLength(36).IsRequired();
            e.Property(x => x.SubscriptionName).HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(x => x.ServiceName).HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(x => x.ResourceGroupName).HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(x => x.Tag).HasMaxLength(512).HasDefaultValue(string.Empty);
            e.Property(x => x.Cost).HasColumnType("decimal(38,18)");
            e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            e.Property(x => x.NormalizedCost).HasColumnType("decimal(38,18)");

            // Natural key — guarantees one row per dataset/day/sub/grouping/currency so
            // re-collection and historical backfill upsert cleanly (latest write wins).
            e.HasIndex(x => new { x.Dataset, x.UsageDate, x.SubscriptionId, x.ServiceName, x.ResourceGroupName, x.Tag, x.Currency })
                .IsUnique()
                .HasDatabaseName("UX_CostFact_NaturalKey");

            // Common dashboard query shape: a dataset over a date range.
            e.HasIndex(x => new { x.Dataset, x.UsageDate })
                .HasDatabaseName("IX_CostFact_Dataset_UsageDate");
        });

        modelBuilder.Entity<CollectionAuditEntity>(e =>
        {
            e.ToTable("CollectionAudit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Trigger).HasMaxLength(32).IsRequired();
            e.Property(x => x.Error).HasMaxLength(4000);
            e.Property(x => x.ReplicaName).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.StartedUtc).IsDescending().HasDatabaseName("IX_CollectionAudit_StartedUtc");
        });

        modelBuilder.Entity<UserSubscriptionEntity>(e =>
        {
            e.ToTable("UserSubscription");
            e.HasKey(x => x.SubscriptionId);
            e.Property(x => x.SubscriptionId).HasMaxLength(36);
        });

        modelBuilder.Entity<AppSettingEntity>(e =>
        {
            e.ToTable("AppSetting");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(4000).IsRequired();
        });
    }
}
