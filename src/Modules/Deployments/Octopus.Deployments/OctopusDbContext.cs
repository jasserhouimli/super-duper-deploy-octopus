using Microsoft.EntityFrameworkCore;
using Octopus.Apps;
using Octopus.Deployments.Webhooks;

namespace Octopus.Deployments;

/// <summary>
/// Single control-plane DbContext for v0.1 (SQLite file).
/// Apps + Deployments share it to keep ops trivial; split when Postgres lands.
/// </summary>
public sealed class OctopusDbContext(DbContextOptions<OctopusDbContext> options) : DbContext(options)
{
    public DbSet<App> Apps => Set<App>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<AppWebhook> AppWebhooks => Set<AppWebhook>();
    public DbSet<AppEnvVar> AppEnvVars => Set<AppEnvVar>();
    public DbSet<ApiKeys.ApiKey> ApiKeys => Set<ApiKeys.ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<App>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(48).IsRequired();
            e.Property(x => x.RepoUrl).HasMaxLength(500).IsRequired();
            e.Property(x => x.Branch).HasMaxLength(100).IsRequired();
            e.Property(x => x.ContainerName).HasMaxLength(100);
        });
        b.Entity<Deployment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AppId, x.CreatedAt });
            e.Property(x => x.CommitSha).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.ProjectPath).HasMaxLength(300);
            e.Property(x => x.LeaseOwner).HasMaxLength(100);
            e.Property(x => x.LeaseExpiresAt).IsConcurrencyToken();
        });
        b.Entity<WebhookEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AppId, x.DeliveryId }).IsUnique();
            e.HasIndex(x => new { x.AppId, x.ReceivedAt });
            e.Property(x => x.DeliveryId).HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(30).IsRequired();
            e.Property(x => x.Ref).HasMaxLength(200);
            e.Property(x => x.CommitSha).HasMaxLength(64);
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Error).HasMaxLength(500);
        });
        b.Entity<AppWebhook>(e =>
        {
            e.HasKey(x => x.AppId);
            e.Property(x => x.Secret).HasMaxLength(200).IsRequired();
        });
        b.Entity<DeploymentLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeploymentId);
            e.Property(x => x.Line).HasMaxLength(2000).IsRequired();
        });
        b.Entity<AppEnvVar>(e =>
        {
            e.HasKey(x => new { x.AppId, x.Key });
            e.HasIndex(x => x.AppId);
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Value).HasMaxLength(8192).IsRequired();
        });
        b.Entity<ApiKeys.ApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        });
    }
}
