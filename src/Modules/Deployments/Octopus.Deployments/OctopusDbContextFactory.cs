using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Octopus.Deployments;

/// <summary>
/// Design-time factory so `dotnet ef` scaffolds provider-correct migrations.
/// Migrations target PostgreSQL (the production control plane); SQLite keeps
/// the EnsureCreated + DbBootstrap path for zero-setup dev.
/// </summary>
public sealed class OctopusDbContextFactory : IDesignTimeDbContextFactory<OctopusDbContext>
{
    public OctopusDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Octopus")
            ?? "Host=localhost;Username=octopus;Password=postgres;Database=octopus";
        var options = new DbContextOptionsBuilder<OctopusDbContext>();
        OctopusDbOptions.Configure(options, cs);
        return new OctopusDbContext(options.Options);
    }
}
