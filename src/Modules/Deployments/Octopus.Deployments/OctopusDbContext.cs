using Microsoft.EntityFrameworkCore;
using Octopus.Apps;

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
        });
        b.Entity<DeploymentLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeploymentId);
            e.Property(x => x.Line).HasMaxLength(2000).IsRequired();
        });
    }
}
