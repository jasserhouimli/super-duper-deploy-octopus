using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;

namespace Octopus.Tests;

public sealed class DbOptionsTests
{
    [Theory]
    [InlineData("Host=db;Username=octopus;Password=x;Database=octopus", true)]
    [InlineData("host=localhost;database=o", true)]
    [InlineData("Data Source=octopus.db", false)]
    [InlineData("DataSource=:memory:", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Selects_provider_by_connection_string(string? cs, bool postgres)
    {
        var builder = new DbContextOptionsBuilder<OctopusDbContext>();
        OctopusDbOptions.Configure(builder, cs);
        Assert.Equal(postgres, builder.Options.Extensions.Any(e => e.GetType().FullName!.Contains("Npgsql")));
    }
}
