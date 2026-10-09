using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;
using Octopus.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContext<OctopusDbContext>(o =>
    OctopusDbOptions.Configure(o, builder.Configuration.GetConnectionString("Octopus")));
builder.Services.AddHostedService<DeploymentWorker>();

var host = builder.Build();

// Ensure DB exists for `dotnet run` without the API (upgrades older files in place).
using (var scope = host.Services.CreateScope())
{
    DbBootstrap.EnsureUpgraded(scope.ServiceProvider.GetRequiredService<OctopusDbContext>());
}

host.Run();
